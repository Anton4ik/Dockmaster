using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Protocol;
using Dockmaster.Server.Data;

namespace Dockmaster.Server.Services;

/// <summary>
/// Turns what the heartbeats and the metrics history show into events, once when a problem starts and once when it
/// is over: a server gone, a container restarting, unhealthy or out of memory, a host short of memory or disk, a
/// service at its CPU or memory limit, a replica whose memory only grows. What is going on is kept over a restart of
/// the server, so an update does not report it all again.
/// </summary>
public sealed class HealthWatch : BackgroundService
{
    /// <summary>A server that stays away this long is reported (an agent update reconnects within seconds).</summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ContainerRepeat = TimeSpan.FromMinutes(10);
    // after a start, memory the leak check does not look at: the process comes up from a few MB
    private const int WarmUpMinutes = 3 * 60;
    private const string FileName = "health.json";

    private readonly AgentRegistry _agents;
    private readonly MetricsHistory _metrics;
    private readonly ProjectService _projects;
    private readonly Notifier _notifier;
    private readonly JobIndex _jobs;
    private readonly IDbContextFactory<DockmasterDb> _dbf;
    private readonly ILogger<HealthWatch> _log;
    private readonly string _file;
    private readonly object _lock = new();
    private readonly Dictionary<int, DateTime> _offlineSince = new();
    private readonly Dictionary<int, string> _names = new();
    private readonly HashSet<int> _reportedOffline = [];
    private readonly Dictionary<int, Dictionary<string, ContainerInfo>> _lastContainers = new();
    private readonly Dictionary<string, DateTime> _containerNoticed = new();
    // restarts seen before the agent's record of the exit: waited for once
    private readonly HashSet<string> _waited = [];
    // containers reported unhealthy and not healthy again since
    private readonly HashSet<string> _unhealthy = [];
    // problems reported and not over yet: key → since when
    private Dictionary<string, DateTime> _firing = new();
    private HashSet<string> _leaks = [];

    public HealthWatch(AgentRegistry agents, MetricsHistory metrics, ProjectService projects, Notifier notifier, JobIndex jobs, IDbContextFactory<DockmasterDb> dbf,
        IConfiguration config, IHostEnvironment env, ILogger<HealthWatch> log)
    {
        _dbf = dbf;
        _jobs = jobs;
        _agents = agents;
        _metrics = metrics;
        _projects = projects;
        _notifier = notifier;
        _log = log;
        _file = Path.Combine(config["DataDir"] ?? Path.Combine(env.ContentRootPath, "data"), FileName);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Load();
        // servers already away when Dockmaster starts count from when they were last seen; without that read they count
        // from their first change, which is no reason to stop the server
        try
        {
            await using var db = await _dbf.CreateDbContextAsync(stoppingToken);
            foreach (var a in await db.Agents.Select(a => new { a.Id, a.Name, a.LastSeenAt }).ToListAsync(stoppingToken))
                lock (_lock)
                {
                    _names.TryAdd(a.Id, a.Name);
                    if (_agents.Get(a.Id) == null) _offlineSince.TryAdd(a.Id, a.LastSeenAt ?? DateTime.UtcNow);
                }
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { _log.LogWarning(ex, "servers offline at start not read"); }
        _agents.Changed += OnChanged;
        _metrics.MinuteClosed += OnMinute;
        stoppingToken.Register(() =>
        {
            _agents.Changed -= OnChanged;
            _metrics.MinuteClosed -= OnMinute;
        });
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        Save();
    }

    private void OnChanged(int agentId, bool heartbeat)
    {
        var s = _agents.Get(agentId);
        if (s == null)
        {
            lock (_lock)
            {
                _offlineSince.TryAdd(agentId, DateTime.UtcNow);
                _lastContainers.Remove(agentId);
            }
            return;
        }
        bool back;
        DateTime since;
        lock (_lock)
        {
            _names[agentId] = s.Name;
            back = _offlineSince.Remove(agentId, out since) && _reportedOffline.Remove(agentId);
        }
        if (back) Raise(new DmEvent(DmEvent.Info, "server", $"server {s.Name} is back after {Minutes(DateTime.UtcNow - since)}", AgentId: agentId));
        if (heartbeat && s.Last != null) Containers(s);
    }

    /// <summary>
    /// Restarts, health and crashes, from the difference to the previous heartbeat. Not while the container's project
    /// is being deployed: a rollout stops old replicas and drains them (a drain may make them unhealthy on purpose).
    /// Why a container stopped comes from the agent's record of its exit (docker forgets the exit code once it runs
    /// again), with its last log lines; a restart seen before that record is waited for one heartbeat. Unhealthy is
    /// reported when it holds for two heartbeats: a container being stopped fails its last check on the way out,
    /// and that is no news.
    /// </summary>
    private void Containers(AgentSession s)
    {
        var events = new List<(ContainerInfo C, DmEvent E)>();
        lock (_lock)
        {
            var now = s.Last!.Containers.ToDictionary(c => c.Id);
            // what the next heartbeat is compared to: this one, with a copy only when a container is to be compared again
            var seen = now;
            if (_lastContainers.TryGetValue(s.AgentId, out var before))
                foreach (var c in now.Values)
                {
                    if (!before.TryGetValue(c.Id, out var was)) continue;
                    var exit = c.LastExit is { } e && e.UnixMs > (was.LastExit?.UnixMs ?? 0) ? e : null;
                    var restarted = c.RestartCount > was.RestartCount;
                    if (restarted && exit == null && _waited.Add($"{c.Id}:{c.RestartCount}"))
                    {
                        if (ReferenceEquals(seen, now)) seen = new Dictionary<string, ContainerInfo>(now);
                        seen[c.Id] = was; // compared again next time, when the exit may be in
                        continue;
                    }
                    var code = exit?.ExitCode ?? c.ExitCode;
                    var oom = exit?.Oom == true || c.OomKilled;
                    var why = ExitReason(code, oom, c.MemoryLimit);
                    var (what, level, text) =
                        restarted ? ("restart", DmEvent.Warning, $"restarted (restart {c.RestartCount}{(exit != null || code != 0 || oom ? ": " + why : "")})")
                        // stopped by itself: a stop or a rollout ends with 0, SIGTERM (143) or the kill after the grace
                        // time (137) — a 137 from running out of memory is no stop
                        : (exit != null || was.State == "running") && c.State == "exited" && (oom || code is not (0 or 137 or 143)) ? ("exit", DmEvent.Warning, $"stopped: {why}")
                        : c.State == "running" && c.Health == "unhealthy" && was.Health == "unhealthy" && !_unhealthy.Contains(c.Id) ? ("unhealthy", DmEvent.Warning, "is unhealthy")
                        : c.Health == "healthy" && _unhealthy.Contains(c.Id) ? ("healthy", DmEvent.Info, "is healthy again")
                        : ("", "", "");
                    if (what == "") continue;
                    // a job ends by itself every time: the JobScheduler reports its runs (with how long they took)
                    if (what == "exit" && _jobs.IsJob(s.AgentId, c)) continue;
                    if (what == "unhealthy") _unhealthy.Add(c.Id);
                    else if (what == "healthy") _unhealthy.Remove(c.Id);
                    _waited.Remove($"{c.Id}:{c.RestartCount}");
                    var ev = new DmEvent(level, "container", $"{c.Name} on {s.Name} {text}", AgentId: s.AgentId, Service: NullIfEmpty(c.Service),
                        Container: c.Name, Details: what is "restart" or "exit" && exit?.LogTail is { Length: > 0 } tail ? tail : null);
                    var key = c.Id + ":" + what;
                    if (_containerNoticed.TryGetValue(key, out var at) && DateTime.UtcNow - at < ContainerRepeat) continue;
                    _containerNoticed[key] = DateTime.UtcNow;
                    events.Add((c, ev));
                }
            _lastContainers[s.AgentId] = seen;
            if (before != null) _unhealthy.RemoveWhere(id => before.ContainsKey(id) && !now.ContainsKey(id)); // removed containers
            if (_waited.Count > 1000) _waited.Clear();
        }
        foreach (var (c, e) in events)
            _ = Task.Run(async () =>
            {
                var projectId = await _projects.FindAsync(s.AgentId, c.Project, CancellationToken.None);
                if (projectId is { } pid && _projects.IsRunning(pid)) return;
                await _notifier.RaiseAsync(e with { ProjectId = projectId });
            });
    }

    /// <summary>Why a run ended, as a person reads it: "exit code 137 — killed (…)".</summary>
    public static string ExitReason(int code, bool oom, long memoryLimit = 0) =>
        oom ? $"out of memory{(memoryLimit > 0 ? $" (limit {Bytes(memoryLimit)})" : "")}, exit code {code}"
        : $"exit code {code}" + (code switch
        {
            0 => " — it ended by itself",
            1 or 2 => " — an error in the app",
            125 => " — docker could not start it",
            126 => " — its command cannot be run",
            127 => " — its command was not found",
            134 => " — aborted (SIGABRT)",
            137 => " — killed (SIGKILL): out of memory, or it did not stop in time",
            139 => " — crashed (segmentation fault)",
            143 => " — stopped (SIGTERM)",
            > 128 and < 160 => $" — ended by signal {code - 128}",
            _ => "",
        });

    private void OnMinute(long minute) => _ = Task.Run(async () =>
    {
        try
        {
            // notices old enough to be repeated are forgotten (once a minute, not on every heartbeat)
            lock (_lock)
                foreach (var k in _containerNoticed.Where(x => DateTime.UtcNow - x.Value > ContainerRepeat).Select(x => x.Key).ToList()) _containerNoticed.Remove(k);
            await OfflineAsync();
            Hosts(minute);
            await ServicesAsync(minute);
            await LeaksAsync();
        }
        catch (Exception ex) { _log.LogWarning(ex, "health checks for minute {m}", minute); }
    });

    private async Task OfflineAsync()
    {
        List<(int Id, string Name, DateTime Since)> gone;
        lock (_lock)
            gone = _offlineSince.Where(x => DateTime.UtcNow - x.Value >= OfflineAfter && _agents.Get(x.Key) == null && !_reportedOffline.Contains(x.Key))
                .Select(x => (x.Key, _names.GetValueOrDefault(x.Key, $"#{x.Key}"), x.Value)).ToList();
        if (gone.Count == 0) return;
        // a server deleted from Dockmaster is not "offline"
        HashSet<int> known;
        await using (var db = await _dbf.CreateDbContextAsync())
            known = (await db.Agents.Select(a => a.Id).ToListAsync()).ToHashSet();
        lock (_lock)
        {
            foreach (var g in gone.Where(g => !known.Contains(g.Id))) _offlineSince.Remove(g.Id);
            _reportedOffline.UnionWith(gone.Where(g => known.Contains(g.Id)).Select(g => g.Id));
        }
        foreach (var (id, name, since) in gone.Where(g => known.Contains(g.Id)))
            Raise(new DmEvent(DmEvent.Warning, "server", $"server {name} is offline since {since:yyyy-MM-dd HH:mm} UTC", AgentId: id));
    }

    private void Hosts(long minute)
    {
        foreach (var s in _agents.All)
        {
            var points = _metrics.Host(s.AgentId, minute - 4);
            if (points.Count == 0) continue;
            var mem = points.Select(p => p.MemoryTotal > 0 ? 100.0 * p.MemoryUsed / p.MemoryTotal : 0).ToList();
            var last = points[^1];
            var disk = last.DiskTotal > 0 ? 100.0 * last.DiskUsed / last.DiskTotal : 0;
            Threshold($"host-mem:{s.AgentId}", points.Count >= 5 && mem.All(m => m >= 90), mem[^1] < 85,
                () => new DmEvent(DmEvent.Warning, "host", $"server {s.Name}: memory at {mem[^1]:0}% for 5 minutes", AgentId: s.AgentId),
                () => new DmEvent(DmEvent.Info, "host", $"server {s.Name}: memory back to {mem[^1]:0}%", AgentId: s.AgentId));
            Threshold($"host-disk:{s.AgentId}", disk >= 90, disk < 85,
                () => new DmEvent(DmEvent.Warning, "host", $"server {s.Name}: docker disk {disk:0}% full ({Bytes(last.DiskTotal - last.DiskUsed)} left)", AgentId: s.AgentId),
                () => new DmEvent(DmEvent.Info, "host", $"server {s.Name}: docker disk back to {disk:0}%", AgentId: s.AgentId));
        }
    }

    /// <summary>
    /// A service at its limits: all its replicas on all the servers of its project together, against their limits
    /// together. Projects of the same name on different servers are different projects.
    /// </summary>
    private async Task ServicesAsync(long minute)
    {
        var series = _metrics.Services(_ => true, minute - 9);
        var projectOf = await _projects.FindAllAsync(series.Keys.Select(k => (k.AgentId, k.Project)), CancellationToken.None);
        var byService = series.Where(x => projectOf.GetValueOrDefault((x.Key.AgentId, x.Key.Project)) != null)
            .GroupBy(x => (ProjectId: projectOf[(x.Key.AgentId, x.Key.Project)]!.Value, x.Key.Project, x.Key.Service))
            .Select(g => (g.Key.ProjectId, g.Key.Project, g.Key.Service, Points: MetricsHistory.Sum(g.Select(x => (IReadOnlyList<ServicePoint>)x.Value))));
        foreach (var (projectId, project, service, points) in byService)
        {
            if (points.Count == 0 || points[^1].Minute < minute) continue; // not running now
            var cpu = points.Where(p => p.CpuLimit > 0).Select(p => p.Cpu / p.CpuLimit).ToList();
            var mem = points.TakeLast(5).Where(p => p.MemoryLimit > 0).Select(p => 100.0 * p.Memory / p.MemoryLimit).ToList();
            var name = $"{project}/{service}";
            Threshold($"svc-cpu:{projectId}/{service}", points.Count >= 10 && cpu.Count == points.Count && cpu.All(c => c >= 90), cpu.Count == 0 || cpu[^1] < 70,
                () => new DmEvent(DmEvent.Warning, "service", $"{name}: CPU at {cpu[^1]:0}% of its limit for 10 minutes", ProjectId: projectId, Service: service),
                () => new DmEvent(DmEvent.Info, "service", $"{name}: CPU back to {(cpu.Count > 0 ? cpu[^1] : 0):0}% of its limit", ProjectId: projectId, Service: service));
            Threshold($"svc-mem:{projectId}/{service}", mem.Count >= 5 && mem.All(m => m >= 90), mem.Count == 0 || mem[^1] < 80,
                () => new DmEvent(DmEvent.Warning, "service", $"{name}: memory at {mem[^1]:0}% of its limit for 5 minutes", ProjectId: projectId, Service: service),
                () => new DmEvent(DmEvent.Info, "service", $"{name}: memory back to {(mem.Count > 0 ? mem[^1] : 0):0}% of its limit", ProjectId: projectId, Service: service));
        }
    }

    /// <summary>
    /// A replica whose lowest memory of the last day is well above the lowest of the day before: most likely a leak.
    /// The lowest point of a whole day, so neither garbage collection saw-teeth nor the daily rise and fall with the
    /// traffic count. Once per container.
    /// </summary>
    private async Task LeaksAsync()
    {
        var tracks = _metrics.Containers();
        lock (_lock) _leaks.IntersectWith(tracks.Select(t => t.Id));
        foreach (var t in tracks)
        {
            if (Leak(t.Lows, t.Started) is not { } growth) continue;
            lock (_lock) if (!_leaks.Add(t.Id)) continue;
            var limit = t.Lows[^1].Limit;
            var left = limit > 0
                ? $" {Bytes(limit - t.Lows[^1].Memory)} left to its limit, about {Hours((limit - t.Lows[^1].Memory) / (growth / 24))} at this pace."
                : "";
            var projectId = await _projects.FindAsync(t.AgentId, t.Project, CancellationToken.None);
            await _notifier.RaiseAsync(new DmEvent(DmEvent.Warning, "memory",
                $"{t.Name}: memory low point rose by {Bytes((long)growth)} day over day — a leak?{left}",
                ProjectId: projectId, AgentId: t.AgentId, Service: NullIfEmpty(t.Service)));
        }
    }

    /// <summary>
    /// Bytes the lowest point of the last 24 hours is above the lowest of the 24 before, when that is at least 64 MB
    /// and 20 %; otherwise null. Both days must be nearly complete (22 hours each), so a container younger than two days
    /// is not judged: caches and heaps fill up after a start. The first hours after the start do not count at all: the
    /// process is still coming up (a few MB), and a day holding them would always be far below the next.
    /// </summary>
    /// <param name="lows">the lowest point of each hour, oldest first</param>
    /// <param name="started">unix minute the container started, 0 = not known</param>
    public static double? Leak(IReadOnlyList<MemoryPoint> lows, long started = 0)
    {
        if (started > 0) lows = lows.Where(p => p.Minute >= started + WarmUpMinutes).ToList();
        if (lows.Count == 0) return null;
        var end = lows[^1].Minute;
        var today = lows.Where(p => p.Minute > end - 24 * 60).Select(p => p.Memory).ToList();
        var yesterday = lows.Where(p => p.Minute > end - 48 * 60 && p.Minute <= end - 24 * 60).Select(p => p.Memory).ToList();
        if (today.Count < 22 || yesterday.Count < 22 || today.Min() <= 0 || yesterday.Min() <= 0) return null;
        double growth = today.Min() - yesterday.Min();
        return growth >= 64L << 20 && growth >= yesterday.Min() * 0.2 ? growth : null;
    }

    /// <summary>Reports a problem when it starts and when it is over (the "over" test is looser, so it does not flap).</summary>
    private void Threshold(string key, bool on, bool off, Func<DmEvent> start, Func<DmEvent> end)
    {
        bool fire, clear;
        lock (_lock)
        {
            var firing = _firing.ContainsKey(key);
            fire = on && !firing;
            clear = firing && off && !on;
            if (fire) _firing[key] = DateTime.UtcNow;
            if (clear) _firing.Remove(key);
        }
        if (fire) Raise(start());
        if (clear) Raise(end());
    }

    private void Raise(DmEvent e) => _ = _notifier.RaiseAsync(e);

    // ---------- what is going on, over a restart ----------

    private sealed record Stored(Dictionary<string, DateTime> Firing, List<string> Leaks);

    private void Save()
    {
        try
        {
            Stored data;
            lock (_lock) data = new Stored(new Dictionary<string, DateTime>(_firing), [.. _leaks]);
            File.WriteAllText(_file, JsonSerializer.Serialize(data));
        }
        catch (Exception ex) { _log.LogWarning(ex, "health state not saved"); }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file) || JsonSerializer.Deserialize<Stored>(File.ReadAllText(_file)) is not { } data) return;
            lock (_lock)
            {
                _firing = data.Firing ?? new();
                _leaks = [.. data.Leaks ?? []];
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "health state not loaded"); }
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
    private static string Minutes(TimeSpan t) => t.TotalMinutes < 1 ? $"{t.TotalSeconds:0} s" : t.TotalHours < 1 ? $"{t.TotalMinutes:0} min" : $"{t.TotalHours:0.#} h";
    private static string Hours(double h) => h < 1 ? $"{h * 60:0} min" : h < 48 ? $"{h:0} h" : $"{h / 24:0} days";

    public static string Bytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0} MB",
        _ => $"{b / 1024.0:0} KB",
    };
}
