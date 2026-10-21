using System.Collections.Concurrent;
using Dockmaster.Protocol;

namespace Dockmaster.Agent.Docker;

// The container list of the heartbeat: state, limits, image freshness and live stats of every container. Its calls
// read their answers straight off the JSON (see EngineApi.Heartbeat): they run for every container on every heartbeat.
public sealed partial class DockerService
{
    // a container docker does not answer about within this long (stuck in a stop) goes out with what is known of it,
    // so one container cannot hold up every heartbeat
    private static readonly TimeSpan ContainerTimeout = TimeSpan.FromSeconds(5);
    // inspects and stats of this many containers at a time
    private const int Parallelism = 8;

    // image inspects are cached: heartbeats run every few seconds for every container
    private readonly ConcurrentDictionary<string, (DateTime at, EngineApi.ImageSummary? image)> _images = new();
    // what the last inspect of every container said, with the list entry it was made for: a container is inspected
    // again when docker's events say it changed (ExitWatcher), when its list entry changed, and when nothing is
    // known of it. Every heartbeat inspected every running container before: two API calls per container per
    // heartbeat, most of them answering the same as last time
    private readonly ConcurrentDictionary<string, (string state, EngineApi.ContainerSummary inspect)> _inspects = new();
    // containers docker reported an event for since they were last inspected
    private readonly ConcurrentDictionary<string, bool> _changed = new();
    // the event stream was (re)connected: what happened while it was away is unknown, so everything is inspected once
    private int _allChanged = 1;
    // the CPU counters of each running container at the last heartbeat: its CPU % is the difference to them
    private readonly ConcurrentDictionary<string, (ulong cpu, ulong system)> _cpu = new();
    private readonly CgroupPeaks _peaks = new();

    /// <summary>Docker reported an event for the container: its next listing inspects it again.</summary>
    public void Changed(string containerId) => _changed[containerId] = true;

    /// <summary>The event stream was lost or (re)connected: every container is inspected again next time.</summary>
    public void AllChanged() => Interlocked.Exchange(ref _allChanged, 1);

    /// <summary>Every container of the host with its live numbers, by name.</summary>
    public async Task<ContainerInfo[]> ListContainersAsync(CancellationToken ct)
    {
        var list = await _engine.ListForHeartbeatAsync(ct);
        var all = Interlocked.Exchange(ref _allChanged, 0) == 1;
        var result = new ContainerInfo[list.Count];
        await Parallel.ForAsync(0, list.Count, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            async (i, token) => result[i] = await DescribeAsync(list[i], all, token));
        Forget(list);
        Array.Sort(result, static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    /// <summary>One container of the heartbeat: its list entry, and (what docker answers in time) its inspect and stats.</summary>
    private async Task<ContainerInfo> DescribeAsync(EngineApi.ListedContainer c, bool all, CancellationToken ct)
    {
        var info = new ContainerInfo
        {
            Id = c.Id,
            Name = c.Name,
            Image = c.Image,
            State = c.State,
            Status = c.Status,
            CreatedUnix = c.Created,
            Project = c.Labels.GetValueOrDefault(ProjectLabel) ?? "",
            Service = c.Labels.GetValueOrDefault(ServiceLabel) ?? "",
        };
        foreach (var p in c.Ports)
        {
            if (p.Ip.StartsWith("::")) continue; // IPv6 duplicates of the same binding
            // "10.77.0.2:32768->5432/tcp" when bound to one address (docker's own notation): the server finds the
            // replicas it balances by the address it published them on
            var host = p.Ip is { Length: > 0 } ip && ip != "0.0.0.0" ? ip + ":" : "";
            var text = p.PublicPort > 0 ? $"{host}{p.PublicPort}->{p.PrivatePort}/{p.Type}" : $"{p.PrivatePort}/{p.Type}";
            if (!info.Ports.Contains(text)) info.Ports.Add(text);
        }
        if (c.Status.Contains("(healthy)")) info.Health = "healthy";
        else if (c.Status.Contains("(unhealthy)")) info.Health = "unhealthy";
        else if (c.Status.Contains("(health: starting)")) info.Health = "starting";

        using var one = CancellationTokenSource.CreateLinkedTokenSource(ct);
        one.CancelAfter(ContainerTimeout);
        try
        {
            if (await InspectCachedAsync(c, all, one.Token) is { } insp)
                await FillInspectAsync(insp, c.Labels.GetValueOrDefault(ComposeImageLabel) ?? "", info, one.Token);
            if (c.State == "running") await FillStatsAsync(c.Id, info, one.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogDebug(ex, "inspect or stats of {Id}", c.Id); }
        return info;
    }

    /// <summary>Drops what is kept of the containers that are gone, or no longer running.</summary>
    private void Forget(List<EngineApi.ListedContainer> list)
    {
        var ids = new HashSet<string>(list.Count);
        var running = new HashSet<string>(list.Count);
        foreach (var c in list)
        {
            ids.Add(c.Id);
            if (c.State == "running") running.Add(c.Id);
        }
        Prune(_inspects, ids);
        Prune(_changed, ids);
        Prune(_cpu, running);
        if (_peaks.Available) _peaks.Keep(running);
    }

    private static void Prune<T>(ConcurrentDictionary<string, T> kept, HashSet<string> keep)
    {
        foreach (var id in kept.Keys) // a snapshot: removing while going through it is fine
            if (!keep.Contains(id)) kept.TryRemove(id, out _);
    }

    /// <summary>After a pull: the next heartbeat must see the new images, not cached inspects of the old ones.</summary>
    public void ForgetImages() => _images.Clear();

    /// <summary>
    /// The container's inspect, from the last one unless docker reported an event for it since, its list entry changed
    /// (state, exit code, health, or the "Up 3 hours" that changes by itself: an inspect every hour or so even when
    /// no event came), or <paramref name="all"/>. Null when docker does not answer and nothing is known of it yet.
    /// </summary>
    private async Task<EngineApi.ContainerSummary?> InspectCachedAsync(EngineApi.ListedContainer c, bool all, CancellationToken ct)
    {
        var state = c.State + "|" + c.Status;
        var known = _inspects.TryGetValue(c.Id, out var k) ? k : default;
        // taken before the inspect: an event during it puts the mark back, and the next listing inspects again
        var changed = _changed.TryRemove(c.Id, out _);
        if (known.inspect != null && !all && !changed && known.state == state) return known.inspect;
        try
        {
            // gone since the list: nothing to say of it
            if (await _engine.InspectSummaryAsync(c.Id, ct) is not { } insp) return null;
            _inspects[c.Id] = (state, insp);
            return insp;
        }
        catch (Exception ex) when (known.inspect != null)
        {
            // a failed inspect falls back to the last one (a restart count dropping to 0 for one heartbeat would look
            // like a restart), and is tried again next time
            _log.LogDebug(ex, "inspect {Id}: using the last one", c.Id);
            if (changed) _changed[c.Id] = true;
            return known.inspect;
        }
    }

    private async Task FillInspectAsync(EngineApi.ContainerSummary insp, string composeImage, ContainerInfo info, CancellationToken ct)
    {
        info.RestartCount = insp.RestartCount;
        info.CpuLimit = insp.CpuLimit;
        info.MemoryLimit = insp.MemoryLimit;
        info.RestartPolicy = insp.RestartPolicy;
        info.RestartMax = insp.RestartMax;
        info.ExitCode = insp.ExitCode;
        info.OomKilled = insp.OomKilled;
        info.StartedUnix = insp.StartedUnix;
        // the list's status ("Up 3 hours (healthy)") is of this moment; the inspect may be minutes old. A stopped
        // container has no health: docker keeps the verdict of its last check (which fails while it shuts down)
        if (info.State == "running" && info.Health.Length == 0 && insp.Health.Length > 0) info.Health = insp.Health;
        info.ImageRef = insp.ImageRef;
        var running = await CachedImageAsync(insp.ImageId, TimeSpan.FromMinutes(5), ct);
        if (running != null) info.RepoDigests.AddRange(running.RepoDigests);
        if (info.ImageRef.Length > 0 && !info.ImageRef.StartsWith("sha256:") && !info.ImageRef.Contains('@')
            && await CachedImageAsync(info.ImageRef, TimeSpan.FromSeconds(30), ct) is { } current)
        {
            info.NewerImageLocal = IsNewerImage(current, insp.ImageId, running, composeImage);
            // the same image under the tag's newer index: the container runs what the tag names, so the registry check
            // on the server finds the tag's digest among its digests (the old index's are often gone)
            if (!info.NewerImageLocal && current.Id != insp.ImageId)
                foreach (var d in current.RepoDigests) if (!info.RepoDigests.Contains(d)) info.RepoDigests.Add(d);
        }
    }

    /// <summary>
    /// Whether a deploy would run a different image than the container does: the tag now names other content. On the
    /// containerd image store ids are digests of OCI indexes, and BuildKit wraps every build (even one all from cache)
    /// in a new index with a fresh provenance attestation, so the ids differ for the same image; compose compares the
    /// platform manifest (its com.docker.compose.image label) and leaves such a container be. So the platform manifests
    /// are compared there: the container's from its own index, else from compose's label. On the classic store the
    /// ids are config digests and say it themselves.
    /// </summary>
    internal static bool IsNewerImage(EngineApi.ImageSummary tag, string containerImageId, EngineApi.ImageSummary? running, string composeImage)
    {
        if (tag.Id == containerImageId) return false;
        if (tag.Platform.Length == 0) return true;
        // the label is copied along when a container is re-created from another image: the image itself goes first
        var mine = running?.Platform is { Length: > 0 } p ? p : composeImage;
        return mine.Length == 0 || (mine != tag.Platform && mine != tag.Id);
    }

    private async Task<EngineApi.ImageSummary?> CachedImageAsync(string nameOrId, TimeSpan ttl, CancellationToken ct)
    {
        if (_images.TryGetValue(nameOrId, out var c) && DateTime.UtcNow - c.at < ttl) return c.image;
        EngineApi.ImageSummary? image = null;
        try { image = await _engine.InspectImageSummaryAsync(nameOrId, ct); }
        catch (Exception ex) { _log.LogDebug(ex, "inspect image {Image}", nameOrId); }
        _images[nameOrId] = (DateTime.UtcNow, image);
        return image;
    }

    /// <summary>
    /// One reading of the container's counters. One-shot: docker answers at once instead of taking two readings a
    /// second apart, and the CPU % is taken against the reading of the last heartbeat (an older docker that ignores
    /// one-shot still sends its own previous reading, and that one is used then).
    /// </summary>
    private async Task FillStatsAsync(string id, ContainerInfo info, CancellationToken ct)
    {
        if (await _engine.StatsAsync(id, ct) is not { } s) return;

        var (lastCpu, lastSystem) = s.PreSystem > 0 ? (s.PreCpu, s.PreSystem) : _cpu.GetValueOrDefault(id);
        _cpu[id] = (s.Cpu, s.System);
        var cpus = s.OnlineCpus > 0 ? s.OnlineCpus : (ulong)Math.Max(1, s.PerCpuCount);
        // a restarted container counts from 0 again: no value this once
        if (lastSystem > 0 && s.System > lastSystem && s.Cpu >= lastCpu)
            info.CpuPercent = Math.Round((double)(s.Cpu - lastCpu) / (s.System - lastSystem) * cpus * 100.0, 2);

        info.MemoryUsage = (long)(s.MemoryUsage > s.InactiveFile ? s.MemoryUsage - s.InactiveFile : s.MemoryUsage);
        info.MemoryPeak = s.MaxUsage > 0 ? (long)s.MaxUsage : _peaks.Peak(id);
        info.NetRx = s.NetRx;
        info.NetTx = s.NetTx;
    }
}
