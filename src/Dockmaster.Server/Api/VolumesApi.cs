using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Protocol;
using Dockmaster.Server.Access;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Server.Api;

/// <summary>/api/agents/{id}/volumes…: the docker volumes of a server; /api/projects/{id}/volumes: the ones a project's servers have.</summary>
public static partial class VolumesApi
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(1);
    // sizes read every volume on the disk
    private static readonly TimeSpan SizesTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CreateTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RemoveTimeout = TimeSpan.FromMinutes(5);

    public static void MapVolumesApi(this RouteGroupBuilder agents)
    {
        // volumes with the containers using them; ?sizes=true also measures them (slow on big disks)
        agents.MapGet("/{id:int}/volumes", async (int id, bool? sizes, AgentRegistry reg, CancellationToken ct) =>
            ApiErrors.AgentJson(await reg.SendAsync(id, new Command { ListVolumes = new ListVolumes { Sizes = sizes == true } }, sizes == true ? SizesTimeout : ListTimeout, ct)))
            .ForServer(Perm.View);

        agents.MapPost("/{id:int}/volumes", async (int id, CreateVolumeRequest req, HttpContext ctx, AgentRegistry reg, CancellationToken ct) =>
        {
            if (Options(req, out var options) is { } invalid) return ApiErrors.Bad(invalid);
            var cmd = new CreateVolume { Name = req.Name.Trim() };
            cmd.Options.Add(options);
            var r = await reg.SendAsync(id, new Command { CreateVolume = cmd }, CreateTimeout, ct);
            if (r.Ok) await ctx.AuditAsync($"made volume {cmd.Name}", agentId: id);
            return r.Ok ? Results.Ok(new { message = r.Message }) : ApiErrors.Agent(r);
        }).ForServer(Perm.Volumes);

        agents.MapPost("/{id:int}/volumes/remove", async (int id, RemoveVolumesRequest req, HttpContext ctx, AgentRegistry reg, CancellationToken ct) =>
        {
            if (req.Names is not { Length: > 0 }) return ApiErrors.Bad("nothing selected");
            var cmd = new RemoveVolumes();
            cmd.Names.AddRange(req.Names);
            var r = await reg.SendAsync(id, new Command { RemoveVolumes = cmd }, RemoveTimeout, ct);
            if (r.Ok) await ctx.AuditAsync($"removed volume(s) {string.Join(", ", req.Names)}", agentId: id);
            return ApiErrors.AgentJson(r);
        }).ForServer(Perm.Volumes);
    }

    /// <summary>/api/agents/{id}/networks (the list is in ContainersApi): new bridge networks and removing unused ones.</summary>
    public static void MapNetworksApi(this RouteGroupBuilder agents)
    {
        agents.MapPost("/{id:int}/networks", async (int id, CreateNetworkRequest req, HttpContext ctx, AgentRegistry reg, CancellationToken ct) =>
        {
            var name = req.Name?.Trim() ?? "";
            if (!NetworkNameRegex().IsMatch(name)) return ApiErrors.Bad("a network name is letters, digits, '_', '.' and '-', e.g. shared");
            var r = await reg.SendAsync(id, new Command { CreateNetwork = new CreateNetwork { Name = name, Internal = req.Internal } }, CreateTimeout, ct);
            if (r.Ok) await ctx.AuditAsync($"made docker network {name}", agentId: id);
            return r.Ok ? Results.Ok(new { message = r.Message }) : ApiErrors.Agent(r);
        }).ForServer(Perm.Server);

        agents.MapPost("/{id:int}/networks/remove", async (int id, RemoveVolumesRequest req, HttpContext ctx, AgentRegistry reg, CancellationToken ct) =>
        {
            if (req.Names is not { Length: > 0 }) return ApiErrors.Bad("nothing selected");
            var cmd = new RemoveNetworks();
            cmd.Names.AddRange(req.Names);
            var r = await reg.SendAsync(id, new Command { RemoveNetworks = cmd }, RemoveTimeout, ct);
            if (r.Ok) await ctx.AuditAsync($"removed docker network(s) {string.Join(", ", req.Names)}", agentId: id);
            return ApiErrors.AgentJson(r);
        }).ForServer(Perm.Server);
    }

    public static void MapProjectVolumesApi(this RouteGroupBuilder projects)
    {
        // what the mount editor offers: the volumes of every server the project is on, and the paths the services'
        // images keep their data in (so a person sees what to mount, and what is lost without a mount)
        projects.MapGet("/{id:int}/volumes", async (int id, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, ProjectService projects, AgentRegistry reg, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Projects.FindAsync([id], ct) is not { } p) return ApiErrors.NotFound("project");
            var plan = await projects.PlanAsync(p, db, ct);
            var services = ComposeServices.Parse(p.ComposeYaml, p.Name);
            var running = projects.ContainersOf(p, plan.AgentIds).Select(x => x.Container).ToList();
            // the image a service runs: what its containers were created from, else the file's (with the .env's values)
            var imageOf = services.ToDictionary(s => s.Name, s => running.FirstOrDefault(c => c.Service == s.Name)?.ImageRef is { Length: > 0 } used
                ? used : ComposeServices.Interpolate(s.Image, p.EnvFile));
            var servers = new List<ProjectVolumesServerDto>();
            var paths = new Dictionary<string, string[]>();
            var access = ctx.Access();
            foreach (var h in plan.Split.Hosts)
            {
                // a server the project is placed on is not one the user sees: its volumes are not theirs to see either
                if (!access.Sees(h.AgentId)) { servers.Add(new ProjectVolumesServerDto(h.AgentId, h.AgentName, "you do not see this server", null)); continue; }
                var cmd = new ListVolumes();
                cmd.Images.AddRange(imageOf.Values.Where(i => i.Length > 0 && !i.Contains('$')).Distinct());
                var r = await reg.SendAsync(h.AgentId, new Command { ListVolumes = cmd }, ListTimeout, ct);
                if (!r.Ok) { servers.Add(new ProjectVolumesServerDto(h.AgentId, h.AgentName, AgentRegistry.ErrorText(r), null)); continue; }
                var payload = JsonNode.Parse(r.Payload);
                servers.Add(new ProjectVolumesServerDto(h.AgentId, h.AgentName, null, payload?["volumes"]));
                foreach (var (image, list) in payload?["images"]?.AsObject() ?? [])
                    paths.TryAdd(image, list?.AsArray().Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToArray() ?? []);
            }
            var imagePaths = imageOf.Where(kv => paths.ContainsKey(kv.Value)).ToDictionary(kv => kv.Key, kv => paths[kv.Value]);
            return Results.Ok(new ProjectVolumesDto(servers.ToArray(), imagePaths));
        }).ForProject(Perm.View);
    }

    /// <summary>The driver options of the local driver for the kind of volume asked for; the error when something is missing.</summary>
    private static string? Options(CreateVolumeRequest req, out Dictionary<string, string> options)
    {
        options = [];
        if (!VolumeName.IsValid(req.Name?.Trim())) return "a volume name is letters, digits, '_', '.' and '-', e.g. app-data";
        var address = req.Address?.Trim() ?? "";
        var path = req.Path?.Trim() ?? "";
        var extra = req.Extra?.Trim().Trim(',') ?? "";
        // o= is a comma separated list: a comma in a value would split it
        if (new[] { address, req.Username ?? "", req.Password ?? "" }.Any(v => v.Contains(',') || v.Any(char.IsControl)) || extra.Any(char.IsControl))
            return "the address, user and password cannot contain commas";
        switch (req.Kind)
        {
            case null or "" or "local":
                return null;
            case "nfs":
                if (address.Length == 0) return "the address of the NFS server is missing, e.g. 192.168.1.10";
                if (!path.StartsWith('/')) return "the exported folder starts with /, e.g. /volume1/backup";
                options["type"] = "nfs";
                options["o"] = $"addr={address},rw" + (req.Version is { Length: > 0 } v ? $",nfsvers={v}" : "") + (extra.Length > 0 ? "," + extra : "");
                options["device"] = ":" + path;
                return null;
            case "cifs":
                if (address.Length == 0) return "the address of the Windows / Samba server is missing, e.g. 192.168.1.10";
                path = path.Trim('/');
                if (path.Length == 0) return "the name of the shared folder is missing, e.g. backup";
                options["type"] = "cifs";
                options["device"] = $"//{address}/{path}";
                options["o"] = $"addr={address}" + (req.Username is { Length: > 0 } u ? $",username={u},password={req.Password}" : ",guest")
                    + $",vers={(req.Version is { Length: > 0 } sv ? sv : "3.0")}" + (extra.Length > 0 ? "," + extra : "");
                return null;
            default:
                return "the kind of volume is local, nfs or cifs";
        }
    }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$")] private static partial Regex NetworkNameRegex();
}
