using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Protocol;
using Dockmaster.Server.Access;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Server.Api;

public static class AgentsApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ImportTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SelfUpdateTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ListImagesTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RemoveImagesTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PruneImagesTimeout = TimeSpan.FromMinutes(5);

    public static void MapAgentsApi(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/agents").RequireAuthorization();

        // the servers the user sees
        g.MapGet("/", async (HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, AgentRegistry reg, ImageUpdateChecker updates, JobIndex jobs, CancellationToken ct) =>
        {
            var access = ctx.Access();
            await using var db = await dbf.CreateDbContextAsync(ct);
            var list = await db.Agents.OrderBy(a => a.Name).ToListAsync(ct);
            return list.Where(a => access.Sees(a.Id)).Select(a => a.ToDto(reg.Get(a.Id), updates, jobs)).ToArray();
        }).ForAnyone();

        g.MapGet("/{id:int}", async (int id, IDbContextFactory<DockmasterDb> dbf, AgentRegistry reg, ImageUpdateChecker updates, JobIndex jobs, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Agents.FindAsync([id], ct) is not { } a) return ApiErrors.NotFound("server");
            var s = reg.Get(id);
            return Results.Ok(new AgentDetailDto(a.ToDto(s, updates, jobs), s?.Last?.Containers.Select(c => c.ToDto(updates, jobs.Info(id, c))).ToArray() ?? []));
        }).ForServer(Perm.View);

        g.MapPost("/", async (NewAgentRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, SettingsService settings, CertificateService certs, CancellationToken ct) =>
        {
            var name = req.Name?.Trim() ?? "";
            if (ValidateName(name) is { } invalid) return ApiErrors.Bad(invalid);
            if (!Uri.TryCreate(req.PublicUrl?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https")
                return ApiErrors.Bad("server URL must be https://host:port");
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Agents.AnyAsync(a => EF.Functions.Collate(a.Name, "NOCASE") == name, ct)) return ApiErrors.Conflict("a server with this name already exists");
            var token = SettingsService.NewToken();
            var agent = new AgentEntity { Name = name, TokenHash = AgentHubService.HashToken(token) };
            db.Agents.Add(agent);
            await db.SaveChangesAsync(ct);
            var url = uri.ToString().TrimEnd('/');
            await settings.SetAsync(SettingsService.AgentPublicUrlKey, url, ct);
            var image = await settings.AgentImageAsync(ct);
            // a login line only for a registry the user stored a login for: public images (ghcr.io) need none
            var needsLogin = ImageRef.ExplicitHost(image) is { } host && ImageRef.RegistryFor(await db.Registries.ToListAsync(ct), host) is { Username.Length: > 0 };
            var command = InstallCommand(url, token, certs.Fingerprint, image, needsLogin);
            await ctx.AuditAsync($"added server {name}", agentId: agent.Id);
            return Results.Ok(new NewAgentResponse(agent.Id, token, certs.FingerprintPretty, command));
        }).ForAdmin();

        g.MapPut("/{id:int}", async (int id, RenameAgentRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, AgentRegistry reg, ImageUpdateChecker updates, JobIndex jobs, CancellationToken ct) =>
        {
            var name = req.Name?.Trim() ?? "";
            if (ValidateName(name) is { } invalid) return ApiErrors.Bad(invalid);
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Agents.FindAsync([id], ct) is not { } a) return ApiErrors.NotFound("server");
            if (await db.Agents.AnyAsync(x => EF.Functions.Collate(x.Name, "NOCASE") == name && x.Id != id, ct)) return ApiErrors.Conflict("a server with this name already exists");
            var before = a.Name;
            a.Name = name;
            await db.SaveChangesAsync(ct);
            if (before != name) await ctx.AuditAsync($"renamed server {before} to {name}", agentId: id);
            if (reg.Get(id) is { } session) session.Name = name;
            reg.Notify(id);
            return Results.Ok(a.ToDto(reg.Get(id), updates, jobs));
        }).ForServer(Perm.Server);

        g.MapDelete("/{id:int}", async (int id, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, AgentRegistry reg, MeshService mesh, AccessService access,
            Notifier notifier, CancellationToken ct) =>
        {
            AgentEntity a;
            await using (var db = await dbf.CreateDbContextAsync(ct))
            {
                if (await db.Agents.FindAsync([id], ct) is not { } found) return ApiErrors.NotFound("server");
                a = found;
                if (await db.Projects.AnyAsync(p => p.AgentId == id, ct)) return ApiErrors.Conflict("the server has projects assigned — delete or move them first");
                var byName = await ProjectService.AgentIdsByNameAsync(db, ct);
                var placed = (await db.Projects.ToListAsync(ct)).Where(p => ProjectService.AgentIdsOf(p, byName).Contains(id)).Select(p => p.Name).ToList();
                if (placed.Count > 0) return ApiErrors.Conflict($"services of {string.Join(", ", placed)} are placed on this server — move them first");
            }
            // out of the mesh first (tears dm0 down when the host is online), then the others stop expecting it;
            // that waits for the agent, so no database context is held open around it
            if (a.InMesh) await mesh.LeaveAsync(id, _ => { }, ct);
            await using (var db = await dbf.CreateDbContextAsync(ct))
            {
                // a server added later under the same id must not come with the old grants
                var granted = await db.UserGrants.Where(x => x.AgentId == id).Select(x => x.UserId).ToListAsync(ct);
                await db.UserGrants.Where(x => x.AgentId == id).ExecuteDeleteAsync(ct);
                await db.Agents.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
                access.Changed(granted);
            }
            reg.Get(id)?.Cts.Cancel();
            // the server is gone: an event about none, which the admins get
            await notifier.AuditAsync(ctx.Access(), $"removed server {a.Name}");
            return Results.Ok();

        }).ForAdmin();

        // take over a compose project that was started on the agent without Dockmaster
        g.MapPost("/{id:int}/import", async (int id, ImportProjectRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, AgentRegistry reg, CancellationToken ct) =>
        {
            var name = req.Project?.Trim() ?? "";
            if (ProjectService.ValidateName(name) is { } err) return ApiErrors.Bad(err);
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.ProjectByNameAsync(id, name, ct) != null) return ApiErrors.Conflict("this project is already in Dockmaster");
            var r = await reg.SendAsync(id, new Command { ImportProject = new ImportProject { Project = name } }, ImportTimeout, ct);
            if (!r.Ok) return ApiErrors.Agent(r);
            var imported = JsonSerializer.Deserialize<ImportedProject>(r.Payload, Json)
                ?? throw new InvalidOperationException("the agent sent an empty import result");
            // the rebuilt variant carries its own header
            var header = imported.Source == "rebuilt from containers" ? "" :
                $"# imported {DateTime.UtcNow:yyyy-MM-dd} from {string.Join(", ", imported.ConfigFiles)} ({imported.Source})\n" +
                "# normalised by `docker compose config`: relative paths are absolute host paths now, variables come from the .env tab\n";
            var p = new ProjectEntity { Name = name, AgentId = id, ComposeYaml = header + imported.Compose, EnvFile = imported.Env };
            db.Projects.Add(p);
            await db.SaveChangesAsync(ct);
            await ctx.AuditAsync($"imported project {name}", p.Id);
            return Results.Ok(new ImportProjectResponse(await ProjectsApi.DtoAsync(ctx, p, db, ct), imported.Warnings));
        }).ForServer(Perm.Edit);

        // the agent pulls its own image and replaces its container (it reconnects with the new version)
        g.MapPost("/{id:int}/update", async (int id, HttpContext ctx, AgentRegistry reg, CancellationToken ct) =>
        {
            var r = await reg.SendAsync(id, new Command { SelfUpdate = new SelfUpdate() }, SelfUpdateTimeout, ct);
            if (r.Ok) await ctx.AuditAsync("updated the agent", agentId: id);
            if (!r.Ok && r.Message == WireText.UnknownCommand)
                return ApiErrors.Bad("this agent cannot update itself yet — run the update commands on the host once (see README)");
            return r.Ok ? Results.Ok(new { message = r.Message }) : ApiErrors.Agent(r);
        }).ForServer(Perm.Server);

        // images on the host with size and users; the agent answers with JSON that goes to the UI as is
        g.MapGet("/{id:int}/images", async (int id, AgentRegistry reg, CancellationToken ct) =>
            ApiErrors.AgentJson(await reg.SendAsync(id, new Command { ListImages = new ListImages() }, ListImagesTimeout, ct))).ForServer(Perm.View);

        g.MapPost("/{id:int}/images/remove", async (int id, RemoveImagesRequest req, HttpContext ctx, AgentRegistry reg, CancellationToken ct) =>
        {
            if (req.Ids is not { Length: > 0 }) return ApiErrors.Bad("nothing selected");
            var cmd = new RemoveImages();
            cmd.Ids.AddRange(req.Ids);
            var r = await reg.SendAsync(id, new Command { RemoveImages = cmd }, RemoveImagesTimeout, ct);
            if (r.Ok) await ctx.AuditAsync($"removed {req.Ids.Length} image(s)", agentId: id);
            return ApiErrors.AgentJson(r);
        }).ForServer(Perm.Server);

        // remove images no container uses (?all=true: also tagged ones, otherwise only untagged leftovers)
        g.MapPost("/{id:int}/images/prune", async (int id, bool? all, HttpContext ctx, AgentRegistry reg, CancellationToken ct) =>
        {
            var r = await reg.SendAsync(id, new Command { PruneImages = new PruneImages { AllUnused = all == true } }, PruneImagesTimeout, ct);
            if (r.Ok) await ctx.AuditAsync(all == true ? "removed all unused images" : "removed untagged unused images", agentId: id);
            return r.Ok ? Results.Ok(new { message = r.Message }) : ApiErrors.Agent(r);
        }).ForServer(Perm.Server);

        g.MapContainersApi();
        g.MapVolumesApi();
        g.MapNetworksApi();
        g.MapVolumeTransferApi();
    }

    /// <summary>
    /// Null when the name will do. It goes into the mesh config of every other host and into compose placements, so
    /// no line breaks or other control characters.
    /// </summary>
    public static string? ValidateName(string name) =>
        name.Length == 0 ? "name is required"
        : name.Length > 100 ? "name is longer than 100 characters"
        : name.Any(char.IsControl) ? "name may not contain line breaks or other control characters"
        : null;

    /// <summary>The `docker run` a new host needs; a private registry needs a login first.</summary>
    private static string InstallCommand(string url, string token, string fingerprint, string image, bool needsLogin)
    {
        var login = needsLogin ? $"docker login {ImageRef.ExplicitHost(image)} && \\\n" : "";
        return login + string.Join(" \\\n", [
            "docker run -d --name dockmaster-agent --restart always",
            // its memory grows with the containers on the host (~130 MB with 40); no --cpus: some kernels (Synology) refuse it
            "  --memory 256m",
            "  -v /var/run/docker.sock:/var/run/docker.sock",
            // same path inside and outside: compose hands project paths (secrets, ./relative binds) to the daemon, which resolves them on the host
            "  -v /var/lib/dockmaster:/var/lib/dockmaster",
            "  -v /etc/hostname:/etc/host_hostname:ro",
            // the containers' memory peaks (memory.peak): the agent's own /sys/fs/cgroup holds only its own cgroup
            "  -v /sys/fs/cgroup:/host/cgroup:ro",
            $"  -e DOCKMASTER_SERVERURL={url}",
            $"  -e DOCKMASTER_TOKEN={token}",
            $"  -e DOCKMASTER_SERVERFINGERPRINT={fingerprint}",
            $"  {image}"]);
    }
}
