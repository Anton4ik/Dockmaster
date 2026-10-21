using System.IO.Compression;
using System.Text.Json;
using Dockmaster.Protocol;

namespace Dockmaster.Server.Services;

/// <summary>One minute of a server.</summary>
/// <param name="Cpu">% of the whole host</param>
public sealed record HostPoint(long Minute, double Cpu, long MemoryUsed, long MemoryTotal, long DiskUsed, long DiskTotal, double Load1);

/// <summary>One minute of a service on one server: its running replicas added up.</summary>
/// <param name="Cpu">% of one core, as docker counts it (200 = two cores busy)</param>
/// <param name="CpuLimit">cores; 0 = a replica has no limit</param>
/// <param name="MemoryLimit">0 = a replica has no limit</param>
/// <param name="Rx">bytes per second received</param>
/// <param name="Tx">bytes per second sent</param>
/// <param name="MemoryPeak">the most memory of the minute (see <see cref="ContainerPoint"/>), the replicas' added up; 0 = not known</param>
public sealed record ServicePoint(long Minute, double Replicas, double Cpu, double CpuLimit, long Memory, long MemoryLimit, double Rx, double Tx, long MemoryPeak = 0);

/// <summary>One minute of one container, by name: what the service point is for its replicas, for this one.</summary>
/// <param name="Cpu">% of one core</param>
/// <param name="CpuLimit">cores; 0 = no limit</param>
/// <param name="MemoryLimit">0 = no limit</param>
/// <param name="MemoryPeak">the most memory of the minute: its highest reading, or what the kernel saw between two
/// readings (its memory.peak rose, page cache counted too; all its limit when it ran out of memory); 0 = not known</param>
public sealed record ContainerPoint(long Minute, double Cpu, double CpuLimit, long Memory, long MemoryLimit, double Rx, double Tx, long MemoryPeak = 0);

/// <summary>The lowest memory of one container in one hour (for leaks: they show in one process, a restart starts over).</summary>
/// <param name="Minute">unix minute the hour starts</param>
public sealed record MemoryPoint(long Minute, long Memory, long Limit);

/// <summary>A container followed for leaks, with the lowest point of each of its last hours since it (re)started.</summary>
/// <param name="Started">unix minute it started, 0 = unknown</param>
public sealed record ContainerTrack(string Id, int AgentId, string Name, string Project, string Service, IReadOnlyList<MemoryPoint> Lows, long Started = 0);

public readonly record struct ServiceKey(int AgentId, string Project, string Service);

/// <summary>A container by its name, so a re-created one (pull &amp; redeploy) goes on with the same chart.</summary>
public readonly record struct ContainerKey(int AgentId, string Name);

/// <summary>
/// The last day of every server, every service and every container (by name) at one point a minute, averaged from the
/// heartbeats (every 5 s), and the lowest point of each hour of every container's memory for two days. Kept in memory and written to the data directory on shutdown, so
/// an update of the server does not start the charts over.
/// </summary>
public sealed class MetricsHistory : BackgroundService
{
    public static readonly TimeSpan Keep = TimeSpan.FromHours(24);
    public static readonly TimeSpan KeepContainers = TimeSpan.FromHours(48);
    private const string FileName = "metrics.json.gz";

    private readonly AgentRegistry _agents;
    private readonly ILogger<MetricsHistory> _log;
    private readonly string _file;
    private readonly object _lock = new();

    private readonly Dictionary<int, List<HostPoint>> _hosts = new();
    private readonly Dictionary<ServiceKey, List<ServicePoint>> _services = new();
    private readonly Dictionary<ContainerKey, List<ContainerPoint>> _named = new();
    private readonly Dictionary<string, Track> _containers = new();
    // the minute being collected: sums of the heartbeats so far
    private readonly Dictionary<int, HostAcc> _hostAcc = new();
    private readonly Dictionary<ServiceKey, ServiceAcc> _serviceAcc = new();
    private readonly Dictionary<ContainerKey, ContainerAcc> _namedAcc = new();
    // the last reading of each container: its counters for bytes per second, and what a heartbeat without stats repeats
    private readonly Dictionary<string, Reading> _last = new();
    private long _closedUpTo = -1;

    /// <summary>After each minute is closed (every server and service has its point for it).</summary>
    public event Action<long>? MinuteClosed;

    public MetricsHistory(AgentRegistry agents, IConfiguration config, IHostEnvironment env, ILogger<MetricsHistory> log)
    {
        _agents = agents;
        _log = log;
        _file = Path.Combine(config["DataDir"] ?? Path.Combine(env.ContentRootPath, "data"), FileName);
    }

    public static long MinuteOf(long unixMs) => unixMs / 60_000;
    public static long MinuteNow => MinuteOf(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Load();
        _agents.Changed += OnChanged;
        stoppingToken.Register(() => _agents.Changed -= OnChanged);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // a few seconds into the next minute: the heartbeats of the last one are in
                var now = DateTimeOffset.UtcNow;
                var next = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero).AddMinutes(1).AddSeconds(3);
                await Task.Delay(next - now, stoppingToken);
                var closed = MinuteNow - 1;
                Close(closed);
                try { MinuteClosed?.Invoke(closed); }
                catch (Exception ex) { _log.LogWarning(ex, "after minute {m}", closed); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        Save();
    }

    private void OnChanged(int agentId, bool heartbeat)
    {
        if (!heartbeat || _agents.Get(agentId)?.Last is not { } hb) return;
        try { Add(agentId, hb); }
        catch (Exception ex) { _log.LogDebug(ex, "metrics of agent {id}", agentId); }
    }

    /// <summary>Adds a heartbeat to the minute it belongs to.</summary>
    public void Add(int agentId, Heartbeat hb)
    {
        var unixMs = hb.UnixMs > 0 ? hb.UnixMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_lock)
        {
            // a heartbeat late for a minute already closed counts for the next one; an agent whose clock runs ahead
            // counts for this minute, not for one to come
            var minute = Math.Max(Math.Min(MinuteOf(unixMs), MinuteNow), _closedUpTo + 1);
            if (hb.Host is { } h)
            {
                var acc = _hostAcc.TryGetValue(agentId, out var a) && a.Minute == minute ? a : Flush(agentId, a, minute);
                acc.N++;
                acc.Cpu += h.CpuPercent;
                acc.Load += h.Load1;
                acc.MemoryUsed += h.MemoryUsed;
                acc.MemoryTotal = h.MemoryTotal;
                acc.DiskUsed = h.DiskUsed;
                acc.DiskTotal = h.DiskTotal;
            }

            // this heartbeat's services: the running replicas added up
            var now = new Dictionary<ServiceKey, (int N, double Cpu, double CpuLimit, bool CpuUnlimited, long Mem, long MemLimit, bool MemUnlimited, double Rx, double Tx, long Peak)>();
            foreach (var c in hb.Containers.Where(c => c.State == "running"))
            {
                if (Read(c, unixMs) is not { } r) continue;

                // every container, compose or not; a stopped one gets no points (a gap in its chart)
                var named = new ContainerKey(agentId, c.Name);
                var ca = _namedAcc.TryGetValue(named, out var open) && open.Minute == minute ? open : Flush(named, open, minute);
                ca.N++;
                ca.Cpu += r.Cpu;
                ca.CpuLimit = Math.Max(0, c.CpuLimit);
                ca.Memory += r.Memory;
                ca.MemoryPeak = Math.Max(ca.MemoryPeak, r.High);
                ca.MemoryLimit = Math.Max(0, c.MemoryLimit);
                ca.Rx += r.RxRate;
                ca.Tx += r.TxRate;

                if (c.Project.Length == 0 || c.Service.Length == 0) continue;
                var key = new ServiceKey(agentId, c.Project, c.Service);
                var s = now.GetValueOrDefault(key);
                now[key] = (s.N + 1, s.Cpu + r.Cpu, s.CpuLimit + c.CpuLimit, s.CpuUnlimited || c.CpuLimit <= 0, s.Mem + r.Memory,
                    s.MemLimit + c.MemoryLimit, s.MemUnlimited || c.MemoryLimit <= 0, s.Rx + r.RxRate, s.Tx + r.TxRate, s.Peak + r.High);

                if (!_containers.TryGetValue(c.Id, out var track)) _containers[c.Id] = track = new Track(c.Id, agentId, c.Name, c.Project, c.Service);
                track.Add(minute, r.Memory, c.MemoryLimit, c.StartedUnix / 60);
            }
            foreach (var (key, s) in now)
            {
                var acc = _serviceAcc.TryGetValue(key, out var a) && a.Minute == minute ? a : Flush(key, a, minute);
                acc.N++;
                acc.Replicas += s.N;
                acc.Cpu += s.Cpu;
                acc.CpuLimit = s.CpuUnlimited ? 0 : s.CpuLimit;
                acc.Memory += s.Mem;
                acc.MemoryPeak = Math.Max(acc.MemoryPeak, s.Peak);
                acc.MemoryLimit = s.MemUnlimited ? 0 : s.MemLimit;
                acc.Rx += s.Rx;
                acc.Tx += s.Tx;
            }
            // a service of this server that has no running replica in this heartbeat counts as zero for it
            foreach (var (key, acc) in _serviceAcc.Where(x => x.Key.AgentId == agentId && x.Value.Minute == minute && !now.ContainsKey(x.Key)))
                acc.N++;
        }
    }

    /// <summary>
    /// The container's CPU, memory and traffic in this heartbeat. A running container always uses some memory: none
    /// means docker did not answer for its stats in time, and its last reading stands in (zeros would pull the minute
    /// down, and counters of 0 followed by the real ones would look like the whole traffic since its start in 5 s).
    /// Null when there is no reading of it yet.
    /// </summary>
    private Reading? Read(ContainerInfo c, long unixMs)
    {
        var known = _last.TryGetValue(c.Id, out var last);
        if (c.MemoryUsage <= 0) return known ? last with { High = last.Memory } : null;
        var (rx, tx) = (0.0, 0.0);
        if (known && unixMs > last.UnixMs && c.NetRx >= last.Rx && c.NetTx >= last.Tx)
        {
            var seconds = (unixMs - last.UnixMs) / 1000.0;
            (rx, tx) = ((c.NetRx - last.Rx) / seconds, (c.NetTx - last.Tx) / seconds);
        }
        var peak = c.MemoryPeak > 0 ? c.MemoryPeak : known ? last.Peak : 0;
        var high = Math.Max(c.MemoryUsage, known ? PeakSince(c, last) : 0);
        return _last[c.Id] = new Reading(c.NetRx, c.NetTx, unixMs, c.CpuPercent, c.MemoryUsage, rx, tx, peak, high);
    }

    /// <summary>
    /// The most memory the kernel saw the container hold since the last reading, 0 when that is not known: the readings
    /// are seconds apart, and a spike between them is what the OOM killer acts on. Its peak since it started rose, or
    /// it started since (its peak is all from after then); it ran out of memory since: it held all of its limit.
    /// </summary>
    private static long PeakSince(ContainerInfo c, Reading last)
    {
        var oom = c.LastExit is { Oom: true } e && e.UnixMs > last.UnixMs && c.MemoryLimit > 0 ? c.MemoryLimit : 0;
        // a peak not known last time may be from hours ago
        var rose = c.MemoryPeak > 0 && (c.StartedUnix * 1000 > last.UnixMs || last.Peak > 0 && c.MemoryPeak > last.Peak) ? c.MemoryPeak : 0;
        return Math.Max(oom, rose);
    }

    /// <param name="Peak">the kernel's peak since it started, 0 = not known</param>
    /// <param name="High">the most it held since the last reading, as far as known: this reading or more</param>
    private readonly record struct Reading(long Rx, long Tx, long UnixMs, double Cpu, long Memory, double RxRate, double TxRate, long Peak, long High);

    /// <summary>Closes every minute before <paramref name="upTo"/> + 1 that is still open and drops what is too old.</summary>
    public void Close(long upTo)
    {
        lock (_lock)
        {
            _closedUpTo = Math.Max(_closedUpTo, upTo);
            foreach (var (id, acc) in _hostAcc.Where(x => x.Value.Minute <= upTo).ToList())
            {
                Append(_hosts, id, acc.Point());
                _hostAcc.Remove(id);
            }
            foreach (var (key, acc) in _serviceAcc.Where(x => x.Value.Minute <= upTo).ToList())
            {
                Append(_services, key, acc.Point());
                _serviceAcc.Remove(key);
            }
            foreach (var (key, acc) in _namedAcc.Where(x => x.Value.Minute <= upTo).ToList())
            {
                Append(_named, key, acc.Point());
                _namedAcc.Remove(key);
            }
            var oldest = upTo - (long)Keep.TotalMinutes;
            DropOld(_hosts, p => p.Minute, oldest);
            DropOld(_services, p => p.Minute, oldest);
            DropOld(_named, p => p.Minute, oldest);
            var oldestContainer = upTo - (long)KeepContainers.TotalMinutes;
            foreach (var t in _containers.Values) DropOld(t.Lows, p => p.Minute, oldestContainer);
            foreach (var id in _containers.Where(x => x.Value.Lows.Count == 0).Select(x => x.Key).ToList()) _containers.Remove(id);
            // the counters of containers not seen running for a while (removed, re-created, stopped)
            var stale = (upTo - 10) * 60_000;
            foreach (var id in _last.Where(x => x.Value.UnixMs < stale).Select(x => x.Key).ToList()) _last.Remove(id);
        }
    }

    private HostAcc Flush(int agentId, HostAcc? open, long minute)
    {
        if (open != null) Append(_hosts, agentId, open.Point());
        return _hostAcc[agentId] = new HostAcc { Minute = minute };
    }

    private ServiceAcc Flush(ServiceKey key, ServiceAcc? open, long minute)
    {
        if (open != null) Append(_services, key, open.Point());
        return _serviceAcc[key] = new ServiceAcc { Minute = minute };
    }

    private ContainerAcc Flush(ContainerKey key, ContainerAcc? open, long minute)
    {
        if (open != null) Append(_named, key, open.Point());
        return _namedAcc[key] = new ContainerAcc { Minute = minute };
    }

    /// <summary>Drops the points up to <paramref name="oldest"/> from the front (a series is oldest first) and the series left empty.</summary>
    private static void DropOld<TKey, TPoint>(Dictionary<TKey, List<TPoint>> series, Func<TPoint, long> minute, long oldest) where TKey : notnull
    {
        foreach (var list in series.Values) DropOld(list, minute, oldest);
        foreach (var key in series.Where(x => x.Value.Count == 0).Select(x => x.Key).ToList()) series.Remove(key);
    }

    private static void DropOld<TPoint>(List<TPoint> list, Func<TPoint, long> minute, long oldest)
    {
        var n = 0;
        while (n < list.Count && minute(list[n]) <= oldest) n++;
        if (n > 0) list.RemoveRange(0, n);
    }

    private static void Append<TKey, TPoint>(Dictionary<TKey, List<TPoint>> series, TKey key, TPoint point) where TKey : notnull
    {
        if (!series.TryGetValue(key, out var list)) series[key] = list = [];
        list.Add(point);
    }

    public IReadOnlyList<HostPoint> Host(int agentId, long since)
    {
        lock (_lock) return _hosts.TryGetValue(agentId, out var list) ? list.Where(p => p.Minute >= since).ToList() : [];
    }

    /// <summary>The minutes of every service of these servers that match, oldest first.</summary>
    public Dictionary<ServiceKey, List<ServicePoint>> Services(Func<ServiceKey, bool> match, long since)
    {
        lock (_lock)
            return _services.Where(x => match(x.Key)).ToDictionary(x => x.Key, x => x.Value.Where(p => p.Minute >= since).ToList());
    }

    /// <summary>The minutes of one container (by name) on one server, oldest first.</summary>
    public IReadOnlyList<ContainerPoint> Container(int agentId, string name, long since)
    {
        lock (_lock) return _named.TryGetValue(new ContainerKey(agentId, name), out var list) ? list.Where(p => p.Minute >= since).ToList() : [];
    }

    public List<ContainerTrack> Containers()
    {
        lock (_lock) return _containers.Values.Select(t => new ContainerTrack(t.Id, t.AgentId, t.Name, t.Project, t.Service, t.Lows.ToList(), t.Started)).ToList();
    }

    /// <summary>A service over several servers, minute by minute: the servers' points of the same minute added up.</summary>
    public static List<ServicePoint> Sum(IEnumerable<IReadOnlyList<ServicePoint>> series) =>
        series.SelectMany(s => s).GroupBy(p => p.Minute).OrderBy(g => g.Key).Select(g => new ServicePoint(g.Key, g.Sum(p => p.Replicas), g.Sum(p => p.Cpu),
            g.Any(p => p.CpuLimit <= 0) ? 0 : g.Sum(p => p.CpuLimit), g.Sum(p => p.Memory), g.Any(p => p.MemoryLimit <= 0) ? 0 : g.Sum(p => p.MemoryLimit),
            g.Sum(p => p.Rx), g.Sum(p => p.Tx), g.Sum(p => p.MemoryPeak))).ToList();

    // ---------- keeping it over a restart ----------

    // Named is missing in a file written before containers had their own charts; Containers of a file written when
    // they were minutes, not hourly lows, have no Lows and start over
    private sealed record Stored(Dictionary<int, List<HostPoint>> Hosts, List<StoredService> Services, List<ContainerTrack> Containers, List<StoredContainer>? Named = null);
    private sealed record StoredService(int AgentId, string Project, string Service, List<ServicePoint> Points);
    private sealed record StoredContainer(int AgentId, string Name, List<ContainerPoint> Points);

    private void Save()
    {
        try
        {
            Stored data;
            lock (_lock)
                data = new Stored(_hosts.ToDictionary(x => x.Key, x => x.Value.ToList()),
                    _services.Select(x => new StoredService(x.Key.AgentId, x.Key.Project, x.Key.Service, x.Value.ToList())).ToList(),
                    _containers.Values.Select(t => new ContainerTrack(t.Id, t.AgentId, t.Name, t.Project, t.Service, t.Lows.ToList(), t.Started)).ToList(),
                    _named.Select(x => new StoredContainer(x.Key.AgentId, x.Key.Name, x.Value.ToList())).ToList());
            var tmp = _file + ".tmp";
            using (var fs = File.Create(tmp))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                JsonSerializer.Serialize(gz, data);
            File.Move(tmp, _file, true);
        }
        catch (Exception ex) { _log.LogWarning(ex, "metrics history not saved"); }
    }

    private void Load()
    {
        if (!File.Exists(_file)) return;
        try
        {
            Stored? data;
            using (var fs = File.OpenRead(_file))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                data = JsonSerializer.Deserialize<Stored>(gz);
            if (data == null) return;
            lock (_lock)
            {
                foreach (var (id, points) in data.Hosts) _hosts[id] = points;
                foreach (var s in data.Services) _services[new ServiceKey(s.AgentId, s.Project, s.Service)] = s.Points;
                foreach (var c in data.Named ?? []) _named[new ContainerKey(c.AgentId, c.Name)] = c.Points;
                foreach (var t in data.Containers.Where(t => t.Lows is { Count: > 0 }))
                {
                    var track = new Track(t.Id, t.AgentId, t.Name, t.Project, t.Service) { Started = t.Started };
                    track.Lows.AddRange(t.Lows);
                    _containers[t.Id] = track;
                }
            }
            Close(MinuteNow - 1);
        }
        catch (Exception ex) { _log.LogWarning(ex, "metrics history not loaded, starting over"); }
    }

    // ---------- the minute being collected ----------

    private sealed class HostAcc
    {
        public long Minute;
        public int N;
        public double Cpu, Load;
        public long MemoryUsed, MemoryTotal, DiskUsed, DiskTotal;
        public HostPoint Point() => new(Minute, Math.Round(Cpu / Math.Max(1, N), 1), MemoryUsed / Math.Max(1, N), MemoryTotal, DiskUsed, DiskTotal, Math.Round(Load / Math.Max(1, N), 2));
    }

    private sealed class ServiceAcc
    {
        public long Minute;
        public int N;
        public double Replicas, Cpu, CpuLimit, Rx, Tx;
        public long Memory, MemoryLimit, MemoryPeak;
        public ServicePoint Point()
        {
            var n = Math.Max(1, N);
            return new(Minute, Math.Round(Replicas / n, 2), Math.Round(Cpu / n, 1), CpuLimit, Memory / n, MemoryLimit, Math.Round(Rx / n), Math.Round(Tx / n), MemoryPeak);
        }
    }

    private sealed class ContainerAcc
    {
        public long Minute;
        public int N;
        public double Cpu, CpuLimit, Rx, Tx;
        public long Memory, MemoryLimit, MemoryPeak;
        public ContainerPoint Point()
        {
            var n = Math.Max(1, N);
            return new(Minute, Math.Round(Cpu / n, 1), CpuLimit, Memory / n, MemoryLimit, Math.Round(Rx / n), Math.Round(Tx / n), MemoryPeak);
        }
    }

    private sealed class Track(string id, int agentId, string name, string project, string service)
    {
        public string Id { get; } = id;
        public int AgentId { get; } = agentId;
        public string Name { get; } = name;
        public string Project { get; } = project;
        public string Service { get; } = service;
        public List<MemoryPoint> Lows { get; } = [];
        public long Started { get; set; }

        /// <summary>The hour's lowest reading; the last point is updated while its hour lasts. A restart starts over.</summary>
        public void Add(long minute, long memory, long limit, long started)
        {
            if (started > 0 && started != Started)
            {
                if (Started > 0) Lows.Clear();
                Started = started;
            }
            var hour = minute - minute % 60;
            if (Lows.Count > 0 && Lows[^1].Minute == hour) Lows[^1] = new MemoryPoint(hour, Math.Min(Lows[^1].Memory, memory), limit);
            else Lows.Add(new MemoryPoint(hour, memory, limit));
        }
    }
}
