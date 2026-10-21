using Microsoft.EntityFrameworkCore;
using Dockmaster.Server.Access;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Server.Api;

/// <summary>
/// /api/projects/{id}/services/{service}/…: quick edits of one service that patch the compose text in place (comments
/// survive); the UI deploys afterwards.
/// </summary>
public static class ProjectServicesApi
{
    public static void MapProjectServicesApi(this RouteGroupBuilder g)
    {
        // which server runs a service (x-dockmaster.agent in the compose file); null = the project's default server
        g.MapPut("/{id:int}/services/{service}/placement", async (int id, string service, PlacementRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf,
            ProjectService projects, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Projects.FindAsync([id], ct) is not { } p) return ApiErrors.NotFound("project");
            var ids = (req.AgentIds ?? (req.AgentId is { } one ? [one] : [])).Distinct().ToList();
            var names = new List<string>();
            // only the project's own server: nothing to write
            var ownServer = ids.Count == 0 || ids is [var only] && only == p.AgentId;
            if (!ownServer)
                foreach (var agentId in ids)
                {
                    if (await db.Agents.FindAsync([agentId], ct) is not { } a) return ApiErrors.Bad("choose a server");
                    if (!a.InMesh) return ApiErrors.Bad($"{a.Name} is not in the mesh — a service on another server needs it (Network page)");
                    names.Add(a.Name);
                }
            if (!ComposeEditor.TryEdit(p.ComposeYaml, y => ComposeEditor.SetPlacement(y, service, names), out var yaml, out var refused)) return ApiErrors.Bad(refused);
            if (await DeniedAsync(ctx, p, yaml, db, ct) is { } denied) return denied;
            await SaveAsync(ctx, db, p, yaml, $"placed {service} on " + (names.Count > 0 ? string.Join(", ", names) : "the project's server"), ct);
            return Results.Ok(await ProjectsApi.PlacementAsync(ctx, p, db, projects, ct));
        }).ForProject(Perm.Edit);

        // the ports a service offers to the other services of the project (compose `expose`), wherever they run
        g.MapPut("/{id:int}/services/{service}/expose", async (int id, string service, ExposeRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf,
            ProjectService projects, CancellationToken ct) =>
        {
            var ports = (req.Ports ?? []).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
            if (!ports.All(ComposeSplitter.IsExposePort)) return ApiErrors.Bad("ports look like 5432 or 53/udp");
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Projects.FindAsync([id], ct) is not { } p) return ApiErrors.NotFound("project");
            if (!ComposeEditor.TryEdit(p.ComposeYaml, y => ComposeEditor.SetSequence(y, service, "expose", ports), out var yaml, out var refused)) return ApiErrors.Bad(refused);
            await SaveAsync(ctx, db, p, yaml, $"set the ports {service} offers", ct);
            return Results.Ok(await ProjectsApi.PlacementAsync(ctx, p, db, projects, ct));
        }).ForProject(Perm.Edit);

        // where a service keeps its data: its `volumes:` list, and the top-level volumes they need
        g.MapPut("/{id:int}/services/{service}/mounts", async (int id, string service, MountsRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf,
            ProjectService projects, CancellationToken ct) =>
        {
            var mounts = (req.Mounts ?? []).Select(m => m.Keep != null ? m : m with { Source = m.Source?.Trim() ?? "", Target = m.Target?.Trim() ?? "", ExternalName = m.ExternalName?.Trim() }).ToList();
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Projects.FindAsync([id], ct) is not { } p) return ApiErrors.NotFound("project");
            if (ComposeServices.Parse(p.ComposeYaml, p.Name).FirstOrDefault(s => s.Name == service) is not { } svc) return ApiErrors.NotFound("service");
            if (ComposeMounts.Validate(mounts, svc.Mounts) is { } invalid) return ApiErrors.Bad(invalid);
            if (!ComposeEditor.TryEdit(p.ComposeYaml, y => ComposeEditor.SetMounts(y, p.Name, service, mounts), out var yaml, out var refused)) return ApiErrors.Bad(refused);
            await SaveAsync(ctx, db, p, yaml, $"set the data of {service}", ct);
            return Results.Ok(await ProjectsApi.PlacementAsync(ctx, p, db, projects, ct));
        }).ForProject(Perm.Edit);

        // image, published ports, environment and restart policy of a service, from its form
        g.MapPut("/{id:int}/services/{service}/settings", async (int id, string service, ServiceSettingsRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf,
            ProjectService projects, JobIndex jobs, CancellationToken ct) =>
        {
            var image = req.Image?.Trim();
            var ports = req.Ports?.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            var env = req.Environment?.Select(e => new EnvVar(e.Name?.Trim() ?? "", e.Value ?? "")).Where(e => e.Name.Length > 0).ToList();
            if (InvalidSettings(image, ports, env, req.Restart) is { } invalid) return ApiErrors.Bad(invalid);
            var (schedule, badSchedule) = ScheduleOf(req.Schedule);
            if (badSchedule != null) return ApiErrors.Bad(badSchedule);
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Projects.FindAsync([id], ct) is not { } p) return ApiErrors.NotFound("project");
            // the form showed the values as (hidden) to a user without Secrets: those keep what is stored
            if (env != null && !await ProjectsApi.SeesSecretsAsync(ctx, p, db, ct))
                env = ComposeEnv.Restore(env, ComposeServices.Parse(p.ComposeYaml, p.Name).FirstOrDefault(s => s.Name == service)?.Environment);
            if (!ComposeEditor.TryEdit(p.ComposeYaml, y =>
                {
                    if (image != null) y = ComposeEditor.SetImage(y, service, image);
                    if (ports != null) y = ComposeEditor.SetPorts(y, service, ports);
                    if (env != null) y = ComposeEditor.SetEnvironment(y, service, env);
                    if (req.Restart != null) y = ComposeEditor.SetRestart(y, service, req.Restart, 0);
                    // after the restart policy: a schedule turns docker's restarts off, whatever the form sent
                    if (req.Schedule != null) y = ComposeEditor.SetSchedule(y, service, schedule);
                    return y;
                }, out var yaml, out var refused)) return ApiErrors.Bad(refused);
            await SaveAsync(ctx, db, p, yaml, $"changed the settings of {service}", ct);
            if (req.Schedule != null) await jobs.RefreshAsync(ct);
            return Results.Ok(await ProjectsApi.PlacementAsync(ctx, p, db, projects, ct));
        }).ForProject(Perm.Edit);

        // the schedule of a job (x-dockmaster.schedule): when Dockmaster starts its container; no cron takes it off
        g.MapPut("/{id:int}/services/{service}/schedule", async (int id, string service, ScheduleRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf,
            ProjectService projects, JobIndex jobs, CancellationToken ct) =>
        {
            var (schedule, bad) = ScheduleOf(req);
            if (bad != null) return ApiErrors.Bad(bad);
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Projects.FindAsync([id], ct) is not { } p) return ApiErrors.NotFound("project");
            if (!ComposeEditor.TryEdit(p.ComposeYaml, y => ComposeEditor.SetSchedule(y, service, schedule), out var yaml, out var refused)) return ApiErrors.Bad(refused);
            await SaveAsync(ctx, db, p, yaml, schedule != null ? $"scheduled {service}: {schedule.Cron}" : $"took the schedule off {service}", ct);
            await jobs.RefreshAsync(ct);
            return Results.Ok(await ProjectsApi.PlacementAsync(ctx, p, db, projects, ct));
        }).ForProject(Perm.Edit);

        // a cron expression in words and the next times it names, while a form is being filled in
        g.MapGet("/schedule-preview", (string? cron, string? timeZone) =>
        {
            var error = Schedules.Validate(cron, timeZone, null);
            if (error != null) return Results.Ok(new SchedulePreviewDto(false, error, "", [], null));
            Schedules.Zone(timeZone, out var zoneProblem);
            return Results.Ok(new SchedulePreviewDto(true, null, Schedules.Describe(cron!), [.. Schedules.Next(cron!, timeZone, DateTime.UtcNow, 3)], zoneProblem));
        }).ForAnyone();

        // the jobs of a project: schedule, next run, the last runs
        g.MapGet("/{id:int}/jobs", async (int id, JobScheduler scheduler, CancellationToken ct) => Results.Ok(await scheduler.StatusAsync(id, ct))).ForProject(Perm.View);

        // starts a job now, outside its schedule
        g.MapPost("/{id:int}/services/{service}/run", async (int id, string service, HttpContext ctx, JobScheduler scheduler, CancellationToken ct) =>
        {
            var (ok, message) = await scheduler.RunNowAsync(id, service, ct);
            await ctx.AuditAsync($"ran job {service} now" + (ok ? "" : " — failed: " + message), id);
            return ok ? Results.Ok(new JobRunResult(true, message)) : ApiErrors.Bad(message);
        }).ForProject(Perm.Control);

        // a new service: image, ports, environment, restart and its data, appended to the compose file
        g.MapPost("/{id:int}/services", async (int id, NewServiceRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, ProjectService projects, JobIndex jobs, CancellationToken ct) =>
        {
            var name = req.Name?.Trim() ?? "";
            var image = req.Image?.Trim() ?? "";
            var ports = (req.Ports ?? []).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            var env = (req.Environment ?? []).Select(e => new EnvVar(e.Name?.Trim() ?? "", e.Value ?? "")).Where(e => e.Name.Length > 0).ToList();
            var restart = req.Restart?.Trim() ?? "unless-stopped";
            var mounts = (req.Mounts ?? []).Where(m => m.Keep == null).Select(m => m with { Source = m.Source?.Trim() ?? "", Target = m.Target?.Trim() ?? "" }).ToList();
            if (ComposeEditor.ValidateServiceName(name) is { } badName) return ApiErrors.Bad(badName);
            if (InvalidSettings(image, ports, env, restart.Length > 0 ? restart : null) is { } invalid) return ApiErrors.Bad(invalid);
            if (ComposeMounts.Validate(mounts, []) is { } badMount) return ApiErrors.Bad(badMount);
            var (schedule, badSchedule) = ScheduleOf(req.Schedule);
            if (badSchedule != null) return ApiErrors.Bad(badSchedule);
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Projects.FindAsync([id], ct) is not { } p) return ApiErrors.NotFound("project");
            if (!ComposeEditor.TryEdit(p.ComposeYaml, y =>
                {
                    y = ComposeEditor.AddService(y, name, image, restart, ports, env);
                    if (mounts.Count > 0) y = ComposeEditor.SetMounts(y, p.Name, name, mounts);
                    return schedule != null ? ComposeEditor.SetSchedule(y, name, schedule) : y;
                }, out var yaml, out var refused, adds: name)) return ApiErrors.Bad(refused);
            await SaveAsync(ctx, db, p, yaml, $"added service {name}", ct);
            if (schedule != null) await jobs.RefreshAsync(ct);
            return Results.Ok(await ProjectsApi.PlacementAsync(ctx, p, db, projects, ct));
        }).ForProject(Perm.Edit);

        // a service out of the compose file (its volumes' declarations too, when nobody else uses them); the page takes its containers down first
        g.MapDelete("/{id:int}/services/{service}", async (int id, string service, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Projects.FindAsync([id], ct) is not { } p) return ApiErrors.NotFound("project");
            if (!ComposeEditor.TryEdit(p.ComposeYaml, y => ComposeEditor.RemoveService(ComposeEditor.SetMounts(y, p.Name, service, []), service), out var yaml, out var refused,
                    removes: service))
                return ApiErrors.Bad(refused);
            await SaveAsync(ctx, db, p, yaml, $"removed service {service}", ct);
            return Results.Ok(await ProjectsApi.DtoAsync(ctx, p, db, ct));
        }).ForProject(Perm.Edit);

        // the secrets a service reads (/run/secrets/<name>): its `secrets:` list
        g.MapPut("/{id:int}/services/{service}/secrets", async (int id, string service, ServiceSecretsRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, CancellationToken ct) =>
        {
            var names = (req.Names ?? []).Select(n => n.Trim()).Where(n => n.Length > 0).Distinct().ToList();
            if (names.FirstOrDefault(n => ProjectService.ValidateSecretName(n) != null) is { } bad) return ApiErrors.Bad($"'{bad}' is not a secret name");
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Projects.FindAsync([id], ct) is not { } p) return ApiErrors.NotFound("project");
            var svc = ComposeServices.Parse(p.ComposeYaml, p.Name).FirstOrDefault(s => s.Name == service);
            if (svc == null) return ApiErrors.NotFound("service");
            if (svc.Secrets.Order().SequenceEqual(names.Order())) return Results.Ok(await ProjectsApi.DtoAsync(ctx, p, db, ct));
            // a long entry (source / target / mode) cannot be kept by a list of names
            if ((YamlNodes.LoadRoot(p.ComposeYaml).Child("services", service) as YamlDotNet.RepresentationModel.YamlMappingNode).Child("secrets") is YamlDotNet.RepresentationModel.YamlSequenceNode seq
                && seq.Children.Any(c => c is not YamlDotNet.RepresentationModel.YamlScalarNode))
                return ApiErrors.Bad($"the secrets of {service} are written with options; change them in the compose file");
            if (!ComposeEditor.TryEdit(p.ComposeYaml, y => ComposeEditor.SetSequence(y, service, "secrets", names), out var yaml, out var refused)) return ApiErrors.Bad(refused);
            await SaveAsync(ctx, db, p, yaml, $"set the secrets {service} reads: " + (names.Count > 0 ? string.Join(", ", names) : "none"), ct);
            return Results.Ok(await ProjectsApi.DtoAsync(ctx, p, db, ct));
        }).ForProject(Perm.Edit);

        // x-dockmaster.autoscale of a service; no max removes it
        g.MapPut("/{id:int}/services/{service}/autoscale", async (int id, string service, AutoscaleRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf,
            ProjectService projects, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Projects.FindAsync([id], ct) is not { } p) return ApiErrors.NotFound("project");
            AutoscaleSpec? spec = null;
            if (req.Max is { } max)
            {
                var svc = ComposeServices.Parse(p.ComposeYaml).FirstOrDefault(s => s.Name == service);
                if (svc == null) return ApiErrors.NotFound("service");
                if (max < svc.Replicas || max > 50) return ApiErrors.Bad($"max is between the replicas in the file ({svc.Replicas}) and 50");
                var cpu = req.Cpu ?? AutoscaleSpec.DefaultCpu;
                if (cpu is < 10 or > 95) return ApiErrors.Bad("the CPU threshold is between 10 and 95 % of the limit");
                if (req.Mode is not (null or AutoscaleSpec.ModeAuto or AutoscaleSpec.ModeRecommend)) return ApiErrors.Bad("mode is auto or recommend");
                var auto = req.Mode == AutoscaleSpec.ModeAuto;
                var names = new List<string>();
                foreach (var agentId in (req.AgentIds ?? []).Distinct())
                {
                    if (await db.Agents.FindAsync([agentId], ct) is not { } a) return ApiErrors.Bad("choose a server");
                    if (auto && !a.InMesh && agentId != p.AgentId) return ApiErrors.Bad($"{a.Name} is not in the mesh — replicas on another server need it (Network page)");
                    names.Add(a.Name);
                }
                spec = new AutoscaleSpec(max, cpu, [.. names], auto);
            }
            if (req.Ports is not (null or ComposeEditor.PortsBalance or ComposeEditor.PortsRange)) return ApiErrors.Bad($"ports is {ComposeEditor.PortsBalance} or {ComposeEditor.PortsRange}");
            if (!ComposeEditor.TryEdit(p.ComposeYaml, y => ComposeEditor.SetAutoscale(y, service, spec, req.Ports), out var yaml, out var refused)) return ApiErrors.Bad(refused);
            if (await DeniedAsync(ctx, p, yaml, db, ct) is { } denied) return denied;
            await SaveAsync(ctx, db, p, yaml, spec != null ? $"autoscale {service} up to {spec.Max}" : $"took autoscaling off {service}", ct);
            return Results.Ok(await ProjectsApi.PlacementAsync(ctx, p, db, projects, ct));
        }).ForProject(Perm.Edit);

        // replicas / limits; ?live=true also applies the limits to the running containers now
        g.MapPut("/{id:int}/services/{service}/limits", async (int id, string service, ServiceLimitsRequest req, bool? live,
            HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, AgentRegistry reg, ProjectService projects, CancellationToken ct) =>
        {
            var limits = new ComposeEditor.ServiceLimits(req.Replicas, req.Memory, req.Cpus, req.Ports);
            if (ComposeEditor.Validate(limits) is { } err) return ApiErrors.Bad(err);
            if (live == true && LiveLimits.Validate(req.Memory, req.Cpus) is { } liveErr) return ApiErrors.Bad(liveErr);
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Projects.FindAsync([id], ct) is not { } p) return ApiErrors.NotFound("project");
            if (!ComposeEditor.TryEdit(p.ComposeYaml, y => ComposeEditor.SetLimits(y, service, limits), out var yaml, out var refused)) return ApiErrors.Bad(refused);
            await SaveAsync(ctx, db, p, yaml, $"set the replicas and limits of {service}", ct);
            if (live != true) return Results.Ok(new LimitsResult(false, "saved", await ProjectsApi.DtoAsync(ctx, p, db, ct)));
            // the service's containers may sit on another server than the project's default one
            var byName = await ProjectService.AgentIdsByNameAsync(db, ct);
            var running = projects.ContainersOf(p, ProjectService.AgentIdsOf(p, byName)).Where(x => x.Container.Service == service && x.Container.State == "running").ToList();
            var results = new List<(bool ok, string message)>();
            foreach (var group in running.GroupBy(x => x.AgentId))
                results.Add(await LiveLimits.ApplyAsync(reg, group.Key, group.Select(x => x.Container.Id), req.Memory, req.Cpus, ct));
            var (ok, message) = results.Count == 0 ? (false, "no running containers to update")
                : (results.All(r => r.ok), string.Join("; ", results.Select(r => r.message).Distinct()));
            return Results.Ok(new LimitsResult(ok, ok ? message + ", saved in the compose file" : "saved in the compose file, but the live update failed: " + message, await ProjectsApi.DtoAsync(ctx, p, db, ct)));
        }).ForProject(Perm.Edit);
    }

    /// <summary>What the service form sends, checked alike for a new service and a changed one (null parts are left out); null when it is fine.</summary>
    private static string? InvalidSettings(string? image, IReadOnlyList<string>? ports, IReadOnlyList<EnvVar>? env, string? restart)
    {
        if (image != null && ComposeEditor.ValidateImage(image) is { } badImage) return badImage;
        if (ports?.Select(ComposeEditor.ValidatePort).FirstOrDefault(e => e != null) is { } badPort) return badPort;
        if (env != null && ComposeEditor.ValidateEnv(env) is { } badEnv) return badEnv;
        if (restart != null && !ComposeEditor.RestartPolicies.Contains(restart)) return "restart is one of " + string.Join(", ", ComposeEditor.RestartPolicies);
        return null;
    }

    /// <summary>The schedule a form sends: null to take it off (or when the form sent none), or what is wrong with it.</summary>
    private static (ScheduleSpec? Spec, string? Error) ScheduleOf(ScheduleRequest? req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Cron)) return (null, null);
        var tz = req.TimeZone?.Trim();
        var timeout = req.Timeout?.Trim();
        if (Schedules.Validate(req.Cron, tz, timeout) is { } error) return (null, error);
        return (new ScheduleSpec(req.Cron.Trim(), string.IsNullOrEmpty(tz) ? null : tz, string.IsNullOrEmpty(timeout) ? null : timeout), null);
    }

    /// <summary>The project with the edited compose file would touch a server the user may not edit projects on.</summary>
    private static async Task<IResult?> DeniedAsync(HttpContext ctx, ProjectEntity p, string yaml, DockmasterDb db, CancellationToken ct)
    {
        var edited = new ProjectEntity { Id = p.Id, Name = p.Name, AgentId = p.AgentId, ComposeYaml = yaml, DeployedAgentIds = p.DeployedAgentIds, AutoscaleExtra = p.AutoscaleExtra };
        return await ProjectsApi.DeniedAsync(ctx.Access(), edited, db, ct);
    }

    /// <summary>The edited compose file, and what was done to it as an audit event (none when nothing changed).</summary>
    private static async Task SaveAsync(HttpContext ctx, DockmasterDb db, ProjectEntity p, string yaml, string what, CancellationToken ct)
    {
        if (yaml == p.ComposeYaml) return;
        p.ComposeYaml = yaml;
        p.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await ctx.AuditAsync($"project {p.Name}: {what}", p.Id);
    }
}
