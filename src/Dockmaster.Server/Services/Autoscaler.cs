using Microsoft.EntityFrameworkCore;
using Dockmaster.Server.Data;

namespace Dockmaster.Server.Services;

/// <summary>Where the autoscaler stands with one service, for the pages.</summary>
/// <param name="Desired">replicas it runs (auto) or would run (recommend) now</param>
/// <param name="Extra">server name → replicas it added there (auto)</param>
/// <param name="State">what it waits for or why it does nothing</param>
public sealed record AutoscaleStatus(string Service, string Mode, int Min, int Max, int Threshold, int Desired, double Cpu, IReadOnlyDictionary<string, int> Extra, string State,
    DateTime? LastChangeAt, string? LastChange);

/// <summary>
/// Services with <c>x-dockmaster: {autoscale: …}</c>, once a minute: more replicas while the running ones stay above
/// the CPU threshold of their limit, back to deploy.replicas when they calm down (<see cref="AutoscalePolicy"/>).
/// Mode recommend (the default) only reports what it would do, with the load spread over the replicas it would
/// have run; mode auto does it — a replica on the server with the most room (another server first), taken back
/// first when that server needs its memory or CPU for its own containers.
/// </summary>
public sealed class Autoscaler : BackgroundService
{
    private sealed class State
    {
        public int Desired;
        public long LastChange = long.MinValue / 2;
        public DateTime? LastChangeAt;
        public string? LastChangeText;
        public string Status = "";
        public double Cpu;
        /// <summary>A note said once while its condition lasts ("no room", "at max").</summary>
        public string? Noted;
        public long FailedUntil;
        /// <summary>Servers left out of placing a replica until the minute given: a change there failed, or it needed its resources back.</summary>
        public readonly Dictionary<int, long> Avoid = new();
    }

    private readonly IDbContextFactory<DockmasterDb> _dbf;
    private readonly MetricsHistory _metrics;
    private readonly AgentRegistry _agents;
    private readonly ProjectService _projects;
    private readonly Notifier _notifier;
    private readonly ILogger<Autoscaler> _log;
    private readonly Dictionary<(int ProjectId, string Service), State> _states = new();
    private readonly SemaphoreSlim _busy = new(1, 1);

    public Autoscaler(IDbContextFactory<DockmasterDb> dbf, MetricsHistory metrics, AgentRegistry agents, ProjectService projects, Notifier notifier, ILogger<Autoscaler> log)
    {
        _dbf = dbf;
        _metrics = metrics;
        _agents = agents;
        _projects = projects;
        _notifier = notifier;
        _log = log;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void OnMinute(long minute) => _ = Task.Run(() => TickAsync(minute, stoppingToken), CancellationToken.None);
        _metrics.MinuteClosed += OnMinute;
        stoppingToken.Register(() => _metrics.MinuteClosed -= OnMinute);
        return Task.CompletedTask;
    }

    /// <summary>The autoscaled services of a project, as the autoscaler sees them now.</summary>
    public async Task<List<AutoscaleStatus>> StatusAsync(int projectId, CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        if (await db.Projects.FindAsync([projectId], ct) is not { } p) return [];
        var names = await db.AgentNamesAsync(ct);
        var extras = p.Extras;
        var list = new List<AutoscaleStatus>();
        foreach (var svc in ComposeServices.Parse(p.ComposeYaml).Where(s => s.Autoscale != null))
        {
            var spec = svc.Autoscale!;
            State? st;
            lock (_states) st = _states.GetValueOrDefault((p.Id, svc.Name));
            var extra = (extras.GetValueOrDefault(svc.Name) ?? new Dictionary<int, int>()).ToDictionary(x => names.GetValueOrDefault(x.Key, $"#{x.Key}"), x => x.Value);
            var min = svc.Replicas;
            list.Add(new AutoscaleStatus(svc.Name, spec.Mode, min, Math.Max(min, spec.Max), spec.Cpu,
                st?.Desired ?? Math.Min(Math.Max(min, spec.Max), min + (spec.Auto ? extra.Values.Sum() : 0)), st?.Cpu ?? 0,
                spec.Auto ? extra : new Dictionary<string, int>(), st?.Status ?? "waiting for the first minutes of data", st?.LastChangeAt, st?.LastChangeText));
        }
        return list;
    }

    private async Task TickAsync(long minute, CancellationToken ct)
    {
        if (!await _busy.WaitAsync(0, ct)) return; // the last minute is still being worked on (a deploy): skip this one
        try
        {
            List<ProjectEntity> projects;
            List<AgentEntity> agents;
            await using (var db = await _dbf.CreateDbContextAsync(ct))
            {
                projects = await db.Projects.AsNoTracking().Where(p => p.ComposeYaml.Contains("autoscale") || p.AutoscaleExtra != "").ToListAsync(ct);
                agents = await db.Agents.AsNoTracking().ToListAsync(ct);
            }
            foreach (var p in projects)
            {
                if (_projects.IsRunning(p.Id)) continue;
                try { await ProjectAsync(p, agents, minute, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "autoscaling {project}", p.Name); }
            }
        }
        catch (OperationCanceledException) { }
        finally { _busy.Release(); }
    }

    private async Task ProjectAsync(ProjectEntity p, List<AgentEntity> agents, long minute, CancellationToken ct)
    {
        var services = ComposeServices.Parse(p.ComposeYaml);
        var byName = agents.ToDictionary(a => a.Name, a => a.Id, StringComparer.OrdinalIgnoreCase);
        var names = agents.ToDictionary(a => a.Id, a => a.Name);
        var extras = p.Extras;
        var servers = ProjectService.AgentIdsOf(p, services, byName).Concat(p.DeployedIds).Concat(extras.Values.SelectMany(x => x.Keys)).ToHashSet();

        // replicas added while the service was in mode auto, and it is not any more: they go
        foreach (var (service, extra) in extras.Where(x => services.FirstOrDefault(s => s.Name == x.Key)?.Autoscale is not { Auto: true }))
        {
            var r = await _projects.AutoscaleAsync(p.Id, service, new Dictionary<int, int>(), $"autoscale {service}: off, removing its extra replicas", ct);
            if (r.Busy) continue;
            await RaiseAsync(p, service, r.Ok ? DmEvent.Info : DmEvent.Warning, r.Ok
                ? $"autoscaling of {service} is off: the {extra.Values.Sum()} replica(s) it added are removed — deploy the project to apply its layout without the balancers"
                : $"autoscaling of {service} is off, but its extra replicas could not be removed: {r.Message}");
        }

        foreach (var svc in services.Where(s => s.Autoscale != null))
        {
            var spec = svc.Autoscale!;
            var min = svc.Replicas;
            var max = Math.Max(min, spec.Max);
            var extra = spec.Auto ? extras.GetValueOrDefault(svc.Name) ?? new Dictionary<int, int>() : new Dictionary<int, int>();
            State st;
            lock (_states)
            {
                if (!_states.TryGetValue((p.Id, svc.Name), out st!)) _states[(p.Id, svc.Name)] = st = new State { Desired = min };
            }
            // auto: what runs is what is stored; recommend: what it would run, never below the file's count
            st.Desired = spec.Auto ? Math.Min(max, min + extra.Values.Sum()) : Math.Clamp(st.Desired, min, max);

            var points = MetricsHistory.Sum(_metrics.Services(k => k.Project == p.Name && k.Service == svc.Name && servers.Contains(k.AgentId), minute - AutoscalePolicy.InMinutes + 1)
                .Values.Select(v => (IReadOnlyList<ServicePoint>)v));
            var d = AutoscalePolicy.Decide(points, min, max, spec.Cpu, st.Desired, (int)Math.Min(int.MaxValue, minute - st.LastChange));
            st.Cpu = Math.Round(d.Cpu, 1);
            st.Status = d.Why;

            foreach (var gone in st.Avoid.Where(x => x.Value <= minute).Select(x => x.Key).ToList()) st.Avoid.Remove(gone);
            // auto: a server that needs its resources back gets its guest replica taken back first, and no new one soon
            if (spec.Auto && extra.Count > 0 && minute >= st.FailedUntil && minute - st.LastChange >= AutoscalePolicy.OutMinutes
                && Crowded(extra.Keys, names, minute) is { } crowded)
            {
                var less = extra.ToDictionary();
                less[crowded.Id] -= 1;
                st.Avoid[crowded.Id] = minute + AvoidMinutes;
                await ChangeAsync(p, svc.Name, st, minute, st.Desired - 1, less, crowded.Id,
                    $"{crowded.Name} needs its resources ({crowded.Why}): taking back the replica of {svc.Name} there ({st.Desired} → {st.Desired - 1})", ct);
                continue;
            }
            if (d.Step == ScaleStep.None)
            {
                // said once while it lasts: every replica there is, and still too busy
                if (st.Desired >= max && d.Cpu >= spec.Cpu) await NoteOnceAsync(p, svc.Name, st, "max", $"{svc.Name}: {d.Why}");
                else if (st.Noted == "max") st.Noted = null;
                continue;
            }
            if (minute < st.FailedUntil) { st.Status = "the last change failed — trying again later"; continue; }

            if (d.Step == ScaleStep.Out)
            {
                var need = ReplicaMemory(svc, points);
                var allowed = Allowed(svc, spec, byName, p.AgentId);
                var rooms = allowed.Where(id => !st.Avoid.ContainsKey(id)).Select(id => Room(id, names, minute, p.Name, svc.Name)).ToList();
                var (host, why) = AutoscalePolicy.Place(rooms, need);
                if (host == null)
                {
                    st.Status = "no server has room for another replica";
                    await NoteOnceAsync(p, svc.Name, st, "room", $"{svc.Name}: {d.Why}, but no server has room for replica {st.Desired + 1} — {why}");
                    continue;
                }
                var text = $"{svc.Name}: {d.Why} — {(spec.Auto ? "starting" : "would start")} replica {st.Desired + 1} of {max} on {host.Name}";
                if (spec.Auto)
                {
                    var more = extra.ToDictionary();
                    more[host.AgentId] = more.GetValueOrDefault(host.AgentId) + 1;
                    await ChangeAsync(p, svc.Name, st, minute, st.Desired + 1, more, host.AgentId, text, ct);
                }
                else Recommend(p, svc.Name, st, minute, st.Desired + 1, text + " (mode recommend: nothing done)");
            }
            else
            {
                var text = $"{svc.Name}: {d.Why} — {(spec.Auto ? "removing" : "would remove")} a replica ({st.Desired} → {st.Desired - 1})";
                if (spec.Auto)
                {
                    // from the server the autoscaler added the most to, the busiest one first
                    var from = extra.Where(x => x.Value > 0).OrderByDescending(x => x.Value).ThenByDescending(x => HostMemory(x.Key, minute)).First().Key;
                    var less = extra.ToDictionary();
                    less[from] -= 1;
                    await ChangeAsync(p, svc.Name, st, minute, st.Desired - 1, less, null, $"{text} on {names.GetValueOrDefault(from, "?")}", ct);
                }
                else Recommend(p, svc.Name, st, minute, st.Desired - 1, text + " (mode recommend: nothing done)");
            }
        }
    }

    private void Recommend(ProjectEntity p, string service, State st, long minute, int desired, string text)
    {
        st.Desired = desired;
        Changed(st, minute, text);
        _ = RaiseAsync(p, service, DmEvent.Info, text);
    }

    /// <param name="target">the server a new replica goes to: left out for a while when that fails</param>
    private async Task ChangeAsync(ProjectEntity p, string service, State st, long minute, int desired, Dictionary<int, int> extra, int? target, string text, CancellationToken ct)
    {
        var r = await _projects.AutoscaleAsync(p.Id, service, extra, $"autoscale {service} → {desired}", ct);
        if (r.Busy) { st.Status = "waiting for a deploy of the project to finish"; return; }
        if (r.Ok)
        {
            st.Desired = desired;
            Changed(st, minute, text);
            await RaiseAsync(p, service, DmEvent.Info, text);
            return;
        }
        st.FailedUntil = minute + RetryMinutes;
        if (target is { } t) st.Avoid[t] = minute + AvoidMinutes;
        st.Status = $"the last change failed: {r.Message}";
        await RaiseAsync(p, service, DmEvent.Warning, $"{service}: the autoscaler could not go to {desired} replicas — {r.Message}; it tries again in {RetryMinutes} min");
    }

    private const int RetryMinutes = 15;
    private const int AvoidMinutes = 30;

    private static void Changed(State st, long minute, string text)
    {
        st.LastChange = minute;
        st.LastChangeAt = DateTime.UtcNow;
        st.LastChangeText = text;
        st.Noted = null;
    }

    private async Task NoteOnceAsync(ProjectEntity p, string service, State st, string what, string text)
    {
        if (st.Noted == what) return;
        st.Noted = what;
        await RaiseAsync(p, service, DmEvent.Warning, text);
    }

    private Task RaiseAsync(ProjectEntity p, string service, string level, string text) =>
        _notifier.RaiseAsync(new DmEvent(level, "autoscale", text, ProjectId: p.Id, Service: service));

    /// <summary>The servers extra replicas may go to: autoscale.agents, else the ones the service runs on.</summary>
    private static List<int> Allowed(ServiceSummary svc, AutoscaleSpec spec, Dictionary<string, int> byName, int defaultAgent)
    {
        var names = spec.AgentNames.Length > 0 ? spec.AgentNames : svc.AgentNames;
        var ids = names.Select(n => byName.TryGetValue(n, out var id) ? id : 0).Where(id => id > 0).Distinct().ToList();
        return ids.Count > 0 ? ids : [defaultAgent];
    }

    /// <summary>Memory one more replica needs: its limit, or what one uses now when it has none.</summary>
    private static long ReplicaMemory(ServiceSummary svc, IReadOnlyList<ServicePoint> points)
    {
        if (svc.MemoryLimit is { } limit)
            try { return ComposeEditor.ParseMemory(limit); } catch (FormatException) { }
        var last = points.LastOrDefault();
        return last is { Replicas: > 0 } ? (long)(last.Memory / last.Replicas) : 256L << 20;
    }

    /// <summary>A server as a place for a replica, from its last heartbeat and minutes.</summary>
    private HostRoom Room(int agentId, Dictionary<int, string> names, long minute, string project, string service)
    {
        var s = _agents.Get(agentId);
        var hb = s?.Last;
        var containers = hb?.Containers.Where(c => c.State == "running").ToList() ?? [];
        var reserved = containers.Sum(c => c.MemoryLimit > 0 ? c.MemoryLimit : c.MemoryUsage);
        var cpu = _metrics.Host(agentId, minute - AutoscalePolicy.OutMinutes + 1).Select(p => p.Cpu).DefaultIfEmpty(hb?.Host?.CpuPercent ?? 0).Average();
        return new HostRoom(agentId, names.GetValueOrDefault(agentId, $"#{agentId}"), s != null, hb?.Host?.MemoryTotal ?? 0, reserved, cpu,
            containers.Count(c => c.Project == project && c.Service == service), hb?.Host?.MemoryUsed ?? 0);
    }

    private double HostMemory(int agentId, long minute) =>
        _metrics.Host(agentId, minute).Select(p => p.MemoryTotal > 0 ? 100.0 * p.MemoryUsed / p.MemoryTotal : 0).DefaultIfEmpty(0).Last();

    /// <summary>A server with guest replicas whose own memory or CPU stayed above the line for the last minutes.</summary>
    private (int Id, string Name, string Why)? Crowded(IEnumerable<int> hosts, Dictionary<int, string> names, long minute)
    {
        foreach (var id in hosts)
        {
            var pts = _metrics.Host(id, minute - AutoscalePolicy.OutMinutes + 1);
            if (pts.Count < AutoscalePolicy.OutMinutes) continue;
            if (pts.All(x => x.MemoryTotal > 0 && 100.0 * x.MemoryUsed / x.MemoryTotal >= AutoscalePolicy.HostMemoryFull))
                return (id, names.GetValueOrDefault(id, "?"), $"memory at {100.0 * pts[^1].MemoryUsed / pts[^1].MemoryTotal:0}%");
            if (pts.All(x => x.Cpu >= AutoscalePolicy.HostCpuFull))
                return (id, names.GetValueOrDefault(id, "?"), $"CPU at {pts[^1].Cpu:0}%");
        }
        return null;
    }
}
