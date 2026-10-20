using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Protocol;

namespace Dockmaster.Server.Services;

/// <summary>
/// Getting the most out of the tunnels: the MTU of every host from the packets its paths really carry (Tune speed
/// on the Network page), and a watch on the handshakes, so a tunnel that silently stopped working is reported.
/// </summary>
public sealed partial class MeshService
{
    // the agent pings each peer up to nine times, a second each, the peers side by side
    private static readonly TimeSpan TuneTimeout = TimeSpan.FromSeconds(90);
    private const int ParallelTunes = 4;
    /// <summary>A tunnel both ends of which are up and that had no handshake this long is down: WireGuard renews one every two minutes of traffic, and the status pings every minute.</summary>
    public static readonly TimeSpan StaleHandshake = TimeSpan.FromMinutes(5);
    // reported once it stayed stale this long after it was first seen so (a pair that just joined gets a moment)
    private static readonly TimeSpan ReportStaleAfter = TimeSpan.FromMinutes(2);

    private sealed record PathMtu(string PublicKey, string Host, int Mtu);

    /// <summary>
    /// Every online host measures the largest packet the path to each of its peers carries; the smallest becomes its
    /// tunnel MTU (minus WireGuard's overhead) and the new configs go out. A fixed MTU in Settings wins over it.
    /// </summary>
    public async Task<(bool ok, string message)> TuneAsync(Action<string> onLine, CancellationToken ct)
    {
        var snap = await FreshSnapshotAsync(ct);
        var online = snap.Nodes.Values.Where(n => snap.Online(n.AgentId)).OrderBy(n => n.Name, StringComparer.Ordinal).ToList();
        if (online.Count == 0) return (false, "no server of the mesh is online");
        var byKey = snap.Nodes.Values.DistinctBy(n => n.PublicKey).ToDictionary(n => n.PublicKey, n => n.Name);
        var measured = new ConcurrentDictionary<int, int>();
        onLine($"measuring the paths of {online.Count} server(s): pings that must not be fragmented, up to a minute");
        await Parallel.ForEachAsync(online, new ParallelOptions { MaxDegreeOfParallelism = ParallelTunes, CancellationToken = ct }, async (n, token) =>
        {
            var r = await _agents.SendAsync(n.AgentId, new Command { MeshTune = new MeshTune() }, TuneTimeout, token);
            if (!r.Ok)
            {
                NoteUnsupported(n.AgentId, r);
                onLine($"{n.Name}: {(r.Message == WireText.UnknownCommand ? "the agent is too old to measure — update it" : AgentRegistry.ErrorText(r))}");
                return;
            }
            List<PathMtu> paths;
            try { paths = JsonSerializer.Deserialize<List<PathMtu>>(r.Payload, Json) ?? []; }
            catch (JsonException) { onLine($"{n.Name}: unreadable answer"); return; }
            var known = paths.Where(p => p.Mtu > 0).OrderBy(p => p.Mtu).ThenBy(p => p.Host, StringComparer.Ordinal).ToList();
            if (known.Count == 0)
            {
                onLine($"{n.Name}: no path could be measured (no handshake yet, or the peers drop pings) — keeps MTU {MeshPlanner.Mtu(n, snap.Settings)}");
                return;
            }
            var min = known[0].Mtu;
            measured[n.AgentId] = min;
            var mtu = MeshPlanner.Mtu(n with { PathMtu = min }, snap.Settings);
            onLine($"{n.Name}: {string.Join(", ", known.Select(p => $"to {byKey.GetValueOrDefault(p.PublicKey, p.Host)} {p.Mtu}"))} → tunnel MTU {mtu}"
                + (mtu == MeshPlanner.Mtu(n, snap.Settings) ? " (as now)" : $" (was {MeshPlanner.Mtu(n, snap.Settings)})"));
        });
        if (measured.IsEmpty) return (false, "no path could be measured");
        await using (var db = await _dbf.CreateDbContextAsync(ct))
            foreach (var (id, mtu) in measured)
                await db.Agents.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.MeshPathMtu, mtu), ct);
        foreach (var id in measured.Keys) _agents.Notify(id);
        await PushAllAsync(onLine, ct);
        return (true, $"paths of {measured.Count} server(s) measured"
            + (snap.Settings.Mtu > 0 ? $" — the MTU {snap.Settings.Mtu} set by hand in Settings stays in force; clear it there to use the measured values" : ""));
    }

    // tunnels without a handshake: (from, to) → since when, and the ones reported already
    private readonly Dictionary<(int From, int To), DateTime> _staleSince = new();
    private readonly HashSet<(int From, int To)> _staleReported = [];

    /// <summary>
    /// A tunnel between two online hosts that had no handshake for <see cref="StaleHandshake"/> is down (a UDP port
    /// closed since, an endpoint that changed): one warning while it lasts, a note when it is back.
    /// </summary>
    private async Task WatchHandshakesAsync()
    {
        var snap = _cached;
        if (snap == null) return;
        var status = StatusNow(snap.Agents);
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var idOf = snap.Nodes.Values.DistinctBy(n => n.PublicKey).ToDictionary(n => n.PublicKey, n => n.AgentId);
        var stale = new Dictionary<(int From, int To), (string Peer, long Age)>();
        foreach (var (id, cfg) in snap.Plan.Configs)
        {
            if (status.GetValueOrDefault(id) is not { Up: true } st) continue;
            foreach (var peer in cfg.Peers)
            {
                if (!idOf.TryGetValue(peer.PublicKey, out var to) || status.GetValueOrDefault(to) is not { Up: true }) continue;
                var seen = st.Peers.FirstOrDefault(p => p.PublicKey == peer.PublicKey);
                var age = seen is { LastHandshakeUnix: > 0 } ? nowUnix - seen.LastHandshakeUnix : long.MaxValue;
                if (age >= StaleHandshake.TotalSeconds) stale[(Math.Min(id, to), Math.Max(id, to))] = (peer.Name, age);
            }
        }
        var events = new List<DmEvent>();
        var now = DateTime.UtcNow;
        lock (_staleSince)
        {
            foreach (var k in _staleSince.Keys.Where(k => !stale.ContainsKey(k)).ToList())
            {
                _staleSince.Remove(k);
                if (_staleReported.Remove(k)) events.Add(new DmEvent(DmEvent.Info, "mesh", $"mesh: the tunnel between {snap.Agent(k.From).Name} and {snap.Agent(k.To).Name} is back", AgentId: k.From));
            }
            foreach (var (k, (_, age)) in stale)
            {
                if (!_staleSince.TryGetValue(k, out var since)) _staleSince[k] = since = now;
                if (now - since < ReportStaleAfter || !_staleReported.Add(k)) continue;
                var ago = age == long.MaxValue ? "never" : $"{TimeSpan.FromSeconds(age).TotalMinutes:0} min ago";
                events.Add(new DmEvent(DmEvent.Warning, "mesh",
                    $"mesh: no WireGuard handshake between {snap.Agent(k.From).Name} and {snap.Agent(k.To).Name} (last {ago}) while both are up — the tunnel is down; check that UDP port {snap.Nodes[k.From].ListenPort} / {snap.Nodes[k.To].ListenPort} still reaches the public one and its endpoint on the Network page",
                    AgentId: k.From));
            }
        }
        foreach (var e in events) await _notifier.RaiseAsync(e);
    }
}
