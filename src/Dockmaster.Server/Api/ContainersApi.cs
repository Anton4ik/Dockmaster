using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Protocol;
using Dockmaster.Server.Access;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Server.Api;

/// <summary>/api/agents/{id}/containers/…: actions and limits on single containers.</summary>
public static partial class ContainersApi
{
    private static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RedeployTimeout = TimeSpan.FromMinutes(10);

    // docker's own rule for names, or an id
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$")] private static partial Regex ContainerNameRegex();

    /// <summary>
    /// A change written into a project's compose file reaches every server the project runs on: it needs the
    /// permissions there too, not only on the server of this container. Null when that is fine.
    /// </summary>
    private static async Task<IResult?> ProjectDeniedAsync(HttpContext ctx, AccessService access, ProjectEntity? project, string[] perms, CancellationToken ct) =>
        project != null && (await access.ProjectDeniedAsync(ctx.Access(), project.Id, perms, ct)).Denied is { } denied ? ApiErrors.Forbidden(denied) : null;

    public static void MapContainersApi(this RouteGroupBuilder agents)
    {
        // the container page: config, env, state with the health log, networks, mounts. By name, which survives a re-create.
        agents.MapGet("/{id:int}/containers/{name}/inspect", async (int id, string name, HttpContext ctx, AgentRegistry reg, CancellationToken ct) =>
        {
            if (!ContainerNameRegex().IsMatch(name)) return ApiErrors.Bad("not a container name");
            var r = await reg.SendAsync(id, new Command { InspectContainer = new InspectContainer { Container = name } }, QuickTimeout, ct);
            return r.Ok && !ctx.Access().Can(id, Perm.Secrets) ? Results.Text(WithoutEnvValues(r.Payload), "application/json") : ApiErrors.AgentJson(r);
        }).ForServer(Perm.View);

        // the host's docker networks, for the container page's picker
        agents.MapGet("/{id:int}/networks", async (int id, AgentRegistry reg, CancellationToken ct) =>
            ApiErrors.AgentJson(await reg.SendAsync(id, new Command { ListNetworks = new ListNetworks() }, QuickTimeout, ct))).ForServer(Perm.View);

        // connect a container to a network (created when missing) or disconnect it, live; a container of a Dockmaster
        // project also gets it written into the service's networks, or the next deploy would undo it
        agents.MapPost("/{id:int}/containers/{name}/networks", async (int id, string name, ContainerNetworkRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf,
            AgentRegistry reg, AccessService access, CancellationToken ct) =>
        {
            var network = req.Network?.Trim() ?? "";
            if (!ContainerNameRegex().IsMatch(name)) return ApiErrors.Bad("not a container name");
            if (!ContainerNameRegex().IsMatch(network)) return ApiErrors.Bad("a network name is letters, digits, '_', '.' and '-'");
            var c = reg.Get(id)?.Last?.Containers.FirstOrDefault(x => x.Name == name);
            await using var db = await dbf.CreateDbContextAsync(ct);
            var project = c is { Service.Length: > 0 } ? await db.ProjectByNameAsync(id, c.Project, ct) : null;
            if (await ProjectDeniedAsync(ctx, access, project, [Perm.Edit], ct) is { } denied) return denied;
            var yaml = "";
            // checked before anything changes: a refusal (e.g. the service's last network) leaves the container alone
            if (project != null && !ComposeEditor.TryEdit(project.ComposeYaml, y => ComposeEditor.SetServiceNetwork(y, project.Name, c!.Service, network, req.Connect), out yaml, out var refused))
                return ApiErrors.Bad(refused);
            var r = await reg.SendAsync(id, new Command { ContainerNetwork = new ContainerNetwork { Container = name, Network = network, Connect = req.Connect } }, QuickTimeout, ct);
            if (!r.Ok) return ApiErrors.Agent(r);
            string note;
            if (project != null)
            {
                if (yaml != project.ComposeYaml)
                {
                    project.ComposeYaml = yaml;
                    project.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
                note = SavedNote(reg, id, c!, project);
            }
            else note = c is { Project.Length: > 0 }
                ? $" — compose project {c.Project} is not managed by Dockmaster: its next `docker compose up` undoes this"
                : " — Dockmaster keeps it when it re-creates the container";
            await ctx.AuditAsync($"{(req.Connect ? "connected" : "disconnected")} container {name} {(req.Connect ? "to" : "from")} network {network}", project?.Id, id, name);
            return Results.Ok(new { message = r.Message + note });
        }).ForServer(Perm.Edit);

        // restart policy, live (docker update --restart, no restart). A container of a Dockmaster project: all replicas of
        // its service get it and it is written into the compose file, or the next deploy would undo it.
        agents.MapPost("/{id:int}/containers/{name}/restart-policy", async (int id, string name, ContainerRestartPolicyRequest req, HttpContext ctx,
            IDbContextFactory<DockmasterDb> dbf, AgentRegistry reg, AccessService access, CancellationToken ct) =>
        {
            var policy = req.Policy?.Trim() ?? "";
            var max = policy == "on-failure" ? req.MaxRetries ?? 0 : 0;
            if (!ContainerNameRegex().IsMatch(name)) return ApiErrors.Bad("not a container name");
            if (!ComposeEditor.RestartPolicies.Contains(policy)) return ApiErrors.Bad("restart policy is one of " + string.Join(", ", ComposeEditor.RestartPolicies));
            if (max is < 0 or > 1000) return ApiErrors.Bad("retries must be between 0 (no limit) and 1000");
            var all = reg.Get(id)?.Last?.Containers;
            var c = all?.FirstOrDefault(x => x.Name == name);
            await using var db = await dbf.CreateDbContextAsync(ct);
            var project = c is { Service.Length: > 0 } ? await db.ProjectByNameAsync(id, c.Project, ct) : null;
            if (await ProjectDeniedAsync(ctx, access, project, [Perm.Edit], ct) is { } denied) return denied;
            var yaml = "";
            // checked before anything changes: a compose file Dockmaster cannot edit leaves the containers alone
            if (project != null && !ComposeEditor.TryEdit(project.ComposeYaml, y => ComposeEditor.SetRestart(y, c!.Service, policy, max), out yaml, out var refused))
                return ApiErrors.Bad(refused);
            var targets = project != null ? all!.Where(x => x.Project == c!.Project && x.Service == c.Service).Select(x => x.Name).ToList() : [name];
            string? message = null;
            foreach (var target in targets)
            {
                var r = await reg.SendAsync(id, new Command { ContainerRestartPolicy = new ContainerRestartPolicy { ContainerId = target, Policy = policy, MaxRetries = max } }, QuickTimeout, ct);
                if (!r.Ok) return ApiErrors.Agent(r);
                message ??= r.Message;
            }
            string note;
            if (project != null)
            {
                if (yaml != project.ComposeYaml)
                {
                    project.ComposeYaml = yaml;
                    project.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
                note = $" — saved in project {project.Name}" + (targets.Count > 1 ? $", applied to all {targets.Count} replicas" : "");
            }
            else note = c is { Project.Length: > 0 }
                ? $" — compose project {c.Project} is not managed by Dockmaster: its next `docker compose up` undoes this"
                : " — Dockmaster keeps it when it re-creates the container";
            await ctx.AuditAsync($"set restart policy {policy} of container {name}", project?.Id, id, name);
            return Results.Ok(new { message = message + note });
        }).ForServer(Perm.Edit);

        // change memory / CPU of a running container now; containers of Dockmaster projects also get it written into the compose file
        agents.MapPost("/{id:int}/containers/{cid}/limits", async (int id, string cid, ContainerLimitsRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf,
            AgentRegistry reg, ProjectService projects, AccessService access, CancellationToken ct) =>
        {
            if (reg.FindContainer(id, cid) is not { } c) return ApiErrors.NotFound("container", "server offline?");
            // "" removes a limit; anything else must be a valid value, also when the other one is being removed
            if (ComposeEditor.Validate(new ComposeEditor.ServiceLimits(null, req.Memory is "" ? null : req.Memory, req.Cpus is "" ? null : req.Cpus)) is { } invalid)
                return ApiErrors.Bad(invalid);

            await using var db = await dbf.CreateDbContextAsync(ct);
            var project = c.Service.Length > 0 ? await db.ProjectByNameAsync(id, c.Project, ct) : null;
            var limits = new ComposeEditor.ServiceLimits(null, req.Memory, req.Cpus);
            var removing = req.Memory is "" || req.Cpus is "";
            if (await ProjectDeniedAsync(ctx, access, project, removing ? [Perm.Edit, Perm.Deploy] : [Perm.Edit], ct) is { } denied) return denied;

            if (removing)
            {
                // docker cannot drop a limit from a running container: take it out of the compose file and redeploy that service
                if (project == null)
                    return ApiErrors.Bad("a running container cannot lose its limit; this one is not part of a Dockmaster project, so re-create it without the limit yourself");
                if (!ComposeEditor.TryEdit(project.ComposeYaml, y => ComposeEditor.SetLimits(y, c.Service, limits), out var edited, out var refused)) return ApiErrors.Bad(refused);
                project.ComposeYaml = edited;
                project.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(RedeployTimeout);
                var (deployed, result) = await projects.DeployAsync(project.Id, [c.Service], pull: false, forceRecreate: false, _ => { }, cts.Token, ctx.Access().Name);
                await ctx.AuditAsync($"removed a limit of container {c.Name}, redeployed {c.Service}" + (deployed ? "" : " — failed: " + result), project.Id, id, c.Name);
                return deployed
                    ? Results.Ok(new LimitsResult(false, $"limit removed from the compose file, {c.Service} redeployed", await ProjectsApi.DtoAsync(ctx, project, db, ct)))
                    : ApiErrors.Bad($"limit removed from the compose file, but the redeploy failed: {result} — see the project's history");
            }

            if (LiveLimits.Validate(req.Memory, req.Cpus) is { } err) return ApiErrors.Bad(err);
            var (ok, message) = await LiveLimits.ApplyAsync(reg, id, [cid], req.Memory, req.Cpus, ct);
            if (!ok) return ApiErrors.Bad(message);
            await ctx.AuditAsync($"set limits of container {c.Name}: " + string.Join(", ", new[] { req.Memory is { Length: > 0 } m ? "memory " + m : null, req.Cpus is { Length: > 0 } cpus ? "cpus " + cpus : null }.OfType<string>()),
                project?.Id, id, c.Name);
            if (project == null)
                return Results.Ok(new LimitsResult(true, message + " — not a Dockmaster project, the limit resets when the container is re-created", null));
            if (!ComposeEditor.TryEdit(project.ComposeYaml, y => ComposeEditor.SetLimits(y, c.Service, limits), out var yaml, out var notWritten))
                return Results.Ok(new LimitsResult(true, message + $" — could not write it into the compose file: {notWritten}", null));
            project.ComposeYaml = yaml;
            project.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new LimitsResult(true, message + SavedNote(reg, id, c, project), await ProjectsApi.DtoAsync(ctx, project, db, ct)));
        }).ForServer(Perm.Edit);

        // who started, stopped or removed it goes into the events: a container has no history of its own
        agents.MapPost("/{id:int}/containers/{cid}/{op}", async (int id, string cid, string op, HttpContext ctx, AgentRegistry reg, ProjectService projects,
            Notifier notifier, CancellationToken ct) =>
        {
            ContainerOp? cop = op.ToLowerInvariant() switch
            {
                "start" => ContainerOp.ContainerStart,
                "stop" => ContainerOp.ContainerStop,
                "restart" => ContainerOp.ContainerRestart,
                "kill" => ContainerOp.ContainerKill,
                "remove" => ContainerOp.ContainerRemove,
                _ => null,
            };
            if (cop == null) return ApiErrors.Bad($"unknown action '{op}'");
            var c = reg.FindContainer(id, cid);
            var r = await reg.SendAsync(id, new Command { ContainerAction = new ContainerAction { ContainerId = cid, Op = cop.Value } }, ActionTimeout, ct);
            if (!r.Ok) return ApiErrors.Agent(r);
            var name = c?.Name ?? cid;
            // also a project whose service is only placed on this server
            var projectId = c is { Service.Length: > 0 } ? await projects.FindAsync(id, c.Project, ct) : null;
            await notifier.AuditAsync(ctx.Access(), $"{op.ToLowerInvariant()} container {name}", projectId, id, name);
            return Results.Ok();
        }).ForServer(Perm.Control);
    }

    /// <summary>The value every variable shows to someone without the Secrets permission.</summary>
    public const string HiddenValue = "••••••";

    /// <summary>An inspect answer with the environment's names but not its values (passwords live there); envHidden says so.</summary>
    public static string WithoutEnvValues(string payload)
    {
        var node = JsonNode.Parse(payload);
        if (node is JsonObject root)
        {
            if (root["env"] is JsonArray env)
                foreach (var e in env.OfType<JsonObject>()) e["value"] = HiddenValue;
            root["envHidden"] = true;
        }
        return node?.ToJsonString() ?? payload;
    }

    /// <summary>" — saved in project X", and that the service's other replicas only get the change on the next deploy.</summary>
    private static string SavedNote(AgentRegistry reg, int agentId, ContainerInfo c, ProjectEntity project)
    {
        var siblings = (reg.Get(agentId)?.Last?.Containers.Count(x => x.Project == c.Project && x.Service == c.Service) ?? 1) - 1;
        return $" — saved in project {project.Name}" + (siblings > 0 ? $" (the other {siblings} replica(s) get it on the next deploy)" : "");
    }
}
