using Microsoft.EntityFrameworkCore;
using Dockmaster.Server.Access;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Server.Api;

/// <summary>Events, the metrics history of projects, servers and containers, and where notifications go.</summary>
public static class EventsApi
{
    private const int MaxHours = 24;

    public static void MapEventsApi(this RouteGroupBuilder api)
    {
        // newest first; ?projectId / ?agentId (and ?container, a name on that server) narrow it, ?warnings=true leaves
        // out the info ones, ?kind only one kind (audit: what people did), ?hours only the last ones, ?before=id pages back
        api.MapGet("/events", async (int? projectId, int? agentId, string? container, bool? warnings, string? kind, int? hours, int? before, int? limit, HttpContext ctx,
            IDbContextFactory<DockmasterDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var q = db.Events.AsNoTracking().AsQueryable();
            // the events of the servers the user sees, and of the projects on them; the rest (a test event) is the admins'
            var access = ctx.Access();
            if (!access.IsAdmin)
            {
                var servers = (await db.Agents.Select(a => a.Id).ToListAsync(ct)).Where(access.Sees).ToList();
                var projectIds = await db.Projects.Where(p => servers.Contains(p.AgentId)).Select(p => p.Id).ToListAsync(ct);
                q = q.Where(e => e.AgentId != null && servers.Contains(e.AgentId.Value) || e.ProjectId != null && projectIds.Contains(e.ProjectId.Value));
            }
            if (projectId != null) q = q.Where(e => e.ProjectId == projectId);
            if (agentId != null) q = q.Where(e => e.AgentId == agentId);
            if (!string.IsNullOrEmpty(container)) q = q.Where(e => e.Container == container);
            if (warnings == true) q = q.Where(e => e.Level != DmEvent.Info);
            if (!string.IsNullOrEmpty(kind)) q = q.Where(e => e.Kind == kind);
            if (before != null) q = q.Where(e => e.Id < before);
            if (hours != null)
            {
                var since = DateTime.UtcNow.AddHours(-Math.Max(1, hours.Value));
                q = q.Where(e => e.At >= since);
            }
            var rows = await q.OrderByDescending(e => e.Id).Take(Math.Clamp(limit ?? 100, 1, 500)).ToListAsync(ct);
            var projects = await db.ProjectNamesAsync(ct);
            var agents = await db.AgentNamesAsync(ct);
            return rows.Select(e => Notifier.ToDto(e, e.ProjectId is { } p ? projects.GetValueOrDefault(p) : null, e.AgentId is { } a ? agents.GetValueOrDefault(a) : null)).ToArray();
        }).RequireAuthorization().ForAnyone();

        // every service of the project, its replicas on all its servers added up, one point a minute
        api.MapGet("/projects/{id:int}/metrics", async (int id, int? hours, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, MetricsHistory metrics, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Projects.FindAsync([id], ct) is not { } p) return ApiErrors.NotFound("project");
            // only from the servers the user sees: the project's own is one, others it is placed on may not be
            var servers = AccessService.ProjectServers(p, await ProjectService.AgentIdsByNameAsync(db, ct)).Where(ctx.Access().Sees).ToHashSet();
            var (from, to) = Window(hours);
            var series = metrics.Services(k => k.Project == p.Name && servers.Contains(k.AgentId), from);
            var services = series.GroupBy(x => x.Key.Service).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new ServiceMetricsDto(g.Key, MetricsHistory.Sum(g.Select(x => (IReadOnlyList<ServicePoint>)x.Value))
                    .Select(pt => new[] { pt.Minute, pt.Replicas, pt.Cpu, pt.CpuLimit, pt.Memory, pt.MemoryLimit, pt.Rx, pt.Tx, pt.MemoryPeak }).ToArray())).ToArray();
            return Results.Ok(new ProjectMetricsDto(from, to, services));
        }).RequireAuthorization().ForProject(Perm.View);

        // where the autoscaler stands with each autoscaled service of the project
        api.MapGet("/projects/{id:int}/autoscale", (int id, Autoscaler autoscaler, CancellationToken ct) => autoscaler.StatusAsync(id, ct)).RequireAuthorization()
            .ForProject(Perm.View);

        api.MapGet("/agents/{id:int}/metrics", (int id, int? hours, MetricsHistory metrics) =>
        {
            var (from, to) = Window(hours);
            return new HostMetricsDto(from, to, metrics.Host(id, from)
                .Select(pt => new[] { pt.Minute, pt.Cpu, pt.MemoryUsed, pt.MemoryTotal, pt.DiskUsed, pt.DiskTotal, pt.Load1 }).ToArray());
        }).RequireAuthorization().ForServer(Perm.View);

        // one container by name, over its re-creates
        api.MapGet("/agents/{id:int}/containers/{name}/metrics", (int id, string name, int? hours, MetricsHistory metrics) =>
        {
            var (from, to) = Window(hours);
            return new ContainerMetricsDto(from, to, metrics.Container(id, name, from)
                .Select(pt => new[] { pt.Minute, pt.Cpu, pt.CpuLimit, pt.Memory, pt.MemoryLimit, pt.Rx, pt.Tx, pt.MemoryPeak }).ToArray());
        }).RequireAuthorization().ForServer(Perm.View);

        var n = api.MapGroup("/settings/notifications").RequireAuthorization().ForAdmin();
        n.MapGet("/", async (SettingsService settings, CancellationToken ct) =>
            new NotificationSettingsDto(await settings.GetAsync(Notifier.SeqUrlKey, ct) ?? "", !string.IsNullOrEmpty(await settings.GetAsync(Notifier.SeqApiKeyKey, ct))));

        n.MapPut("/", async (SaveNotificationSettingsRequest req, SettingsService settings, SecretProtector secrets, CancellationToken ct) =>
        {
            var url = (req.SeqUrl ?? "").Trim().TrimEnd('/');
            if (ValidateUrl(url) is { } err) return ApiErrors.Bad(err);
            var values = new Dictionary<string, string> { [Notifier.SeqUrlKey] = url };
            // another Seq: it gets what happens from now on, not the last 30 days
            if (url != (await settings.GetAsync(Notifier.SeqUrlKey, ct) ?? "")) values[Notifier.SeqSinceKey] = DateTime.UtcNow.ToString("O");
            if (req.SeqApiKey != null) values[Notifier.SeqApiKeyKey] = secrets.Protect(req.SeqApiKey.Trim());
            await settings.SetManyAsync(values, ct);
            return Results.Ok();
        });

        // sends a test event to the address in the form, Seq's answer back; without a key typed, the stored one goes
        // along only to the stored address (a typo must not hand it to another host)
        n.MapPost("/test", async (TestNotificationRequest req, SettingsService settings, Notifier notifier, CancellationToken ct) =>
        {
            var url = (req.SeqUrl ?? "").Trim().TrimEnd('/');
            if (url.Length == 0) return ApiErrors.Bad("enter the address of the Seq server");
            if (ValidateUrl(url) is { } err) return ApiErrors.Bad(err);
            var key = req.SeqApiKey;
            if (string.IsNullOrWhiteSpace(key) && url == (await settings.GetAsync(Notifier.SeqUrlKey, ct) ?? "")) key = await notifier.StoredKeyAsync(ct);
            var (ok, error) = await notifier.TestAsync(url, key, ct);
            return ok ? Results.Ok(new { message = "Seq took the test event" }) : ApiErrors.Bad("Seq did not take it: " + error);
        });
    }

    private static (long From, long To) Window(int? hours)
    {
        var to = MetricsHistory.MinuteNow;
        return (to - Math.Clamp(hours ?? MaxHours, 1, MaxHours) * 60, to);
    }

    private static string? ValidateUrl(string url) =>
        url.Length == 0 || (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https") ? null : "the Seq address looks like http://seq:5341";
}
