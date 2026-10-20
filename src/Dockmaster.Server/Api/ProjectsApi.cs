using Microsoft.EntityFrameworkCore;
using Dockmaster.Server.Access;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Server.Api;

public static class ProjectsApi
{
    /// <summary>The shortest health timeout a project can have: a deploy needs a moment to see containers come up.</summary>
    private const int MinHealthTimeoutSeconds = 10;

    public static void MapProjectsApi(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects").RequireAuthorization();

        // the projects of the servers the user sees
        g.MapGet("/", async (HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, ProjectService projects, ImageUpdateChecker updates, JobIndex jobs, CancellationToken ct) =>
        {
            var access = ctx.Access();
            await using var db = await dbf.CreateDbContextAsync(ct);
            var agents = await db.AgentNamesAsync(ct);
            var byName = await ProjectService.AgentIdsByNameAsync(db, ct);
            var list = (await db.Projects.OrderBy(p => p.Name).ToListAsync(ct)).Where(p => access.Sees(p.AgentId)).ToList();
            var templates = await db.Templates.Select(t => new { t.Id, t.Name }).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
            // a failed deploy stops counting once anything succeeded after it (a stop, another deploy): the history keeps it.
            // Only the projects whose last deploy failed need to know
            var failed = list.Where(p => p.LastDeployOk == false).Select(p => p.Id).ToList();
            var lastOk = failed.Count == 0 ? [] : await db.Deployments.Where(d => d.Ok == true && failed.Contains(d.ProjectId)).GroupBy(d => d.ProjectId)
                .Select(g => new { g.Key, At = g.Max(d => d.StartedAt) }).ToDictionaryAsync(x => x.Key, x => x.At, ct);
            return list.Select(p =>
            {
                var superseded = p.LastDeployOk == false && lastOk.TryGetValue(p.Id, out var okAt) && okAt > p.LastDeployedAt;
                var services = ComposeServices.Parse(p.ComposeYaml);
                var placedOn = ProjectService.AgentIdsOf(p, services, byName);
                var cs = projects.ContainersOf(p, placedOn);
                var counts = Mappers.Counts(cs, jobs);
                return new ProjectListItemDto(p.Id, p.Name, p.AgentId, agents.GetValueOrDefault(p.AgentId, "?"),
                    services.Select(s => s.Name).ToArray(),
                    Mappers.Utc(p.UpdatedAt), Mappers.Utc(p.LastDeployedAt), superseded ? null : p.LastDeployOk,
                    counts.Running, counts.Total, counts.Problems,
                    cs.Count(x => updates.Status(x.Container).Status is "outdated" or "redeploy"),
                    placedOn.Select(id => agents.GetValueOrDefault(id, "?")).ToArray(), counts.Jobs,
                    p.TemplateId is { } t ? templates.GetValueOrDefault(t, "?") : null);
            }).ToArray();
        }).ForAnyone();

        // the project split by server: what each server gets and what would stop a deploy
        g.MapGet("/{id:int}/plan", async (int id, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, ProjectService projects, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Projects.FindAsync([id], ct) is not { } p) return ApiErrors.NotFound("project");
            return Results.Ok((await projects.PlanAsync(p, db, ct)).ToDto(hideEnv: !await SeesSecretsAsync(ctx, p, db, ct)));
        }).ForProject(Perm.View);

        g.MapGet("/{id:int}", async (int id, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Projects.FindAsync([id], ct) is not { } p) return ApiErrors.NotFound("project");
            return Results.Ok(await DtoAsync(ctx, p, db, ct));
        }).ForProject(Perm.View);

        g.MapPost("/", async (SaveProjectRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, JobIndex jobs, Notifier notifier, CancellationToken ct) =>
        {
            var access = ctx.Access();
            var name = req.Name?.Trim().ToLowerInvariant() ?? "";
            if (ProjectService.ValidateName(name) is { } err) return ApiErrors.Bad(err);
            if (string.IsNullOrWhiteSpace(req.ComposeYaml)) return ApiErrors.Bad("compose file is empty");
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (!await db.Agents.AnyAsync(a => a.Id == req.AgentId, ct)) return ApiErrors.Bad("choose a server");
            if (await db.ProjectByNameAsync(req.AgentId, name, ct) != null) return ApiErrors.Conflict("this server already has a project with this name");
            var p = new ProjectEntity
            {
                Name = name, AgentId = req.AgentId, ComposeYaml = req.ComposeYaml, EnvFile = access.Can(req.AgentId, Perm.Secrets) ? req.EnvFile ?? "" : "",
                HealthTimeoutSeconds = Math.Max(MinHealthTimeoutSeconds, req.HealthTimeoutSeconds),
            };
            // its own server and every server a service is placed on
            if (await DeniedAsync(access, p, db, ct) is { } denied) return denied;
            db.Projects.Add(p);
            await db.SaveChangesAsync(ct);
            await jobs.RefreshAsync(ct); // a schedule in the file counts from now, not from the next minute
            await notifier.AuditAsync(access, $"added project {p.Name}", p.Id);
            return Results.Ok(await DtoAsync(ctx, p, db, ct));
        }).ForAnyone();

        g.MapPut("/{id:int}", async (int id, SaveProjectRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, ProjectService projects, JobIndex jobs,
            Notifier notifier, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.ComposeYaml)) return ApiErrors.Bad("compose file is empty");
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Projects.FindAsync([id], ct) is not { } p) return ApiErrors.NotFound("project");
            var access = ctx.Access();
            var before = (p.AgentId, p.ComposeYaml, p.EnvFile, p.HealthTimeoutSeconds);
            // without the Secrets permission (on every server it touches, as when it was shown) the .env was not
            // shown, and what came back is not it
            var touched = AccessService.ProjectServers(p, await ProjectService.AgentIdsByNameAsync(db, ct)).Append(req.AgentId);
            if (!touched.All(server => access.Can(server, Perm.Secrets)))
            {
                // nor the values of the environment: the ones sent back as (hidden) keep what is stored
                var (compose, hidden) = ComposeEnv.Restore(req.ComposeYaml, p.ComposeYaml);
                if (compose == null) return ApiErrors.Bad(hidden!);
                req = req with { EnvFile = p.EnvFile, ComposeYaml = compose };
            }
            if (req.UpdatedAt is { } seen && p.UpdatedAt > seen.ToUniversalTime().AddMilliseconds(1)
                && (p.ComposeYaml != req.ComposeYaml || p.EnvFile != (req.EnvFile ?? "")))
                return ApiErrors.Conflict("the project was changed meanwhile somewhere else (a webhook, a rollback, a limit set on the server page) — copy what you typed, reload the page and apply it again");
            if (!await db.Agents.AnyAsync(a => a.Id == req.AgentId, ct)) return ApiErrors.Bad("choose a server");
            if (req.AgentId != p.AgentId && await db.ProjectByNameAsync(req.AgentId, p.Name, ct) != null)
                return ApiErrors.Conflict("that server already has a project with this name");
            p.AgentId = req.AgentId;
            p.ComposeYaml = req.ComposeYaml;
            p.EnvFile = req.EnvFile ?? "";
            p.HealthTimeoutSeconds = Math.Max(MinHealthTimeoutSeconds, req.HealthTimeoutSeconds);
            p.UpdatedAt = DateTime.UtcNow;
            // moved, or a service placed on another server: the user edits projects there too
            if (await DeniedAsync(access, p, db, ct) is { } denied) return denied;
            // a service placed on a server that runs a different project of this name would run it over: refuse now, not at deploy time
            var plan = await projects.PlanAsync(p, db, ct);
            if (plan.NameClashes.Count > 0) return ApiErrors.Conflict(plan.NameClashes[0]);
            await db.SaveChangesAsync(ct);
            await jobs.RefreshAsync(ct);
            var changed = new List<string>();
            if (before.ComposeYaml != p.ComposeYaml) changed.Add("compose file");
            if (before.EnvFile != p.EnvFile) changed.Add(".env");
            if (before.HealthTimeoutSeconds != p.HealthTimeoutSeconds) changed.Add("health timeout");
            if (before.AgentId != p.AgentId) changed.Add("server: " + (await db.Agents.Where(a => a.Id == p.AgentId).Select(a => a.Name).FirstOrDefaultAsync(ct) ?? $"#{p.AgentId}"));
            await notifier.AuditAsync(access, $"saved project {p.Name}" + (changed.Count > 0 ? ": " + string.Join(", ", changed) : " (nothing different)"), p.Id);
            return Results.Ok(await DtoAsync(ctx, p, db, ct));
        }).ForProject(Perm.Edit);

        g.MapDelete("/{id:int}", async (int id, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, Notifier notifier, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Projects.Where(p => p.Id == id).Select(p => new { p.Name, p.AgentId }).FirstOrDefaultAsync(ct) is not { } gone) return ApiErrors.NotFound("project");
            await ProjectService.DeleteRowsAsync(db, id, ct);
            // about its server: the project is gone, the server's people still see who removed it
            await notifier.AuditAsync(ctx.Access(), $"deleted project {gone.Name}", agentId: gone.AgentId);
            return Results.Ok();
        }).ForProject(Perm.Edit);

        g.MapProjectServicesApi();
        g.MapProjectVolumesApi();
        g.MapProjectHookApi();
        g.MapProjectFilesApi();
        g.MapProjectSecretsApi();
    }

    /// <summary>The project as the caller may see it, with what they may do with it (on every server it touches).</summary>
    public static async Task<ProjectDto> DtoAsync(HttpContext ctx, ProjectEntity p, DockmasterDb db, CancellationToken ct) =>
        p.ToDto(ctx.Access(), AccessService.ProjectServers(p, await ProjectService.AgentIdsByNameAsync(db, ct)));

    /// <summary>Whether the user sees the project's secrets: its .env, its files, the values of its environment.</summary>
    public static async Task<bool> SeesSecretsAsync(HttpContext ctx, ProjectEntity p, DockmasterDb db, CancellationToken ct) =>
        Mappers.Has(ctx.Access(), p, AccessService.ProjectServers(p, await ProjectService.AgentIdsByNameAsync(db, ct)), Perm.Secrets);

    /// <summary>The project and its plan after an edit, as this user may see them.</summary>
    public static async Task<PlacementResult> PlacementAsync(HttpContext ctx, ProjectEntity p, DockmasterDb db, ProjectService projects, CancellationToken ct)
    {
        var dto = await DtoAsync(ctx, p, db, ct);
        return new PlacementResult(dto, (await projects.PlanAsync(p, db, ct)).ToDto(hideEnv: dto.EnvHidden));
    }

    /// <summary>
    /// A project as it would be saved needs Edit on every server it touches (its own, the ones services are placed or
    /// autoscaled on): a user cannot put a service on a server that is not theirs. Null when that is fine.
    /// </summary>
    public static async Task<IResult?> DeniedAsync(UserAccess access, ProjectEntity p, DockmasterDb db, CancellationToken ct)
    {
        if (access.IsAdmin) return null;
        var servers = AccessService.ProjectServers(p, await ProjectService.AgentIdsByNameAsync(db, ct));
        return AccessService.Denied(access, servers, [Perm.Edit], await db.AgentNamesAsync(ct)) is { } denied ? ApiErrors.Forbidden(denied) : null;
    }
}
