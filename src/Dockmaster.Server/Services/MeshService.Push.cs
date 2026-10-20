using System.Collections.Concurrent;
using Dockmaster.Protocol;

namespace Dockmaster.Server.Services;

/// <summary>Getting the planned configs onto the hosts: after every change, and on reconnects and heartbeats.</summary>
public sealed partial class MeshService
{
    // the same config again only when the host has not reported it after this long (it applies it, then reports it
    // with its next status); a different config goes out at once
    private static readonly TimeSpan SameConfigAgain = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FailedPushThrottle = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromSeconds(90);
    private const int ParallelPushes = 3;

    private readonly Dictionary<int, (DateTime At, bool Ok, string Hash)> _lastPush = new();
    private readonly SemaphoreSlim _pushing = new(1, 1);

    /// <summary>Every online host whose applied config is not the planned one gets the planned one.</summary>
    public async Task PushAllAsync(Action<string>? onLine, CancellationToken ct)
    {
        await _pushing.WaitAsync(ct);
        try
        {
            var snap = await FreshSnapshotAsync(ct);
            var stale = snap.Plan.Configs.Where(kv => snap.Online(kv.Key) && snap.StatusOf(kv.Key)?.ConfigHash != kv.Value.ConfigHash).ToList();
            await Parallel.ForEachAsync(stale, new ParallelOptions { MaxDegreeOfParallelism = ParallelPushes, CancellationToken = ct }, async (kv, token) =>
            {
                var r = await PushAsync(kv.Key, kv.Value, token);
                onLine?.Invoke($"{snap.Agent(kv.Key).Name}: {AgentRegistry.ErrorText(r)}");
            });
        }
        finally { _pushing.Release(); }
    }

    private async Task<CommandResult> PushAsync(int agentId, MeshApply cfg, CancellationToken ct)
    {
        CommandResult r;
        try { r = await _agents.SendAsync(agentId, new Command { MeshApply = cfg }, ApplyTimeout, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            r = new CommandResult { Ok = false, Message = ex.Message };
        }
        lock (_lastPush) _lastPush[agentId] = (DateTime.UtcNow, r.Ok, cfg.ConfigHash);
        NoteUnsupported(agentId, r);
        if (!r.Ok) _log.LogWarning("mesh config for agent {id} not applied: {m}", agentId, r.Message);
        else _agents.Notify(agentId);
        return r;
    }

    // An agent connected, left, its stored data changed, or its heartbeat says something the plan reads changed (its
    // mesh status, its replicas and the ports they hold): bring every host whose config is behind up to date — not
    // only this one: its containers are the replicas other hosts balance to — without hammering one that keeps
    // failing. A heartbeat that changes nothing the plan reads plans nothing: that planned the whole mesh (every
    // project's compose file parsed) every few seconds for nothing. The timer in ExecuteAsync covers a missed push.
    private void OnChanged(int agentId, bool heartbeat)
    {
        var s = _agents.Get(agentId);
        if (s == null) { _inputs.TryRemove(agentId, out _); _ = ReconcileAsync(); return; }
        if (s.Last is not { } hb) return; // reconciled with its first heartbeat
        if (heartbeat)
        {
            // no host in the mesh: nothing to plan (a join plans afresh)
            if (_cached is { Nodes.Count: 0 }) return;
            var inputs = PlanInputs(hb);
            if (_inputs.TryGetValue(agentId, out var known) && ReferenceEquals(known.Session, s) && known.Inputs == inputs) return;
            _inputs[agentId] = (s, inputs);
        }
        _ = ReconcileAsync();
    }

    // what the plan read from each session's heartbeat last time; a reconnect is a new session and plans again
    private readonly ConcurrentDictionary<int, (AgentSession Session, int Inputs)> _inputs = new();

    /// <summary>
    /// What the plan reads from a heartbeat, hashed: the mesh status (addresses, where peers are seen from, whether
    /// routes are plain) and the running compose replicas with the ports they hold (the balancers' backends).
    /// </summary>
    private static int PlanInputs(Heartbeat hb)
    {
        var h = new HashCode();
        if (hb.Mesh is { } m)
        {
            h.Add(m.Up);
            h.Add(m.PlainRoutes);
            h.Add(m.ConfigHash);
            foreach (var ip in m.HostIps) h.Add(ip);
            foreach (var p in m.Peers) { h.Add(p.PublicKey); h.Add(MeshPlanner.EndpointHost(p.Endpoint)); }
        }
        foreach (var c in hb.Containers)
        {
            if (c.State != "running") continue;
            h.Add(c.Name);
            h.Add(c.ImageRef.Contains("dockmaster-server")); // where the server runs counts as public
            if (c.Project.Length == 0) continue;
            h.Add(c.Project);
            h.Add(c.Service);
            foreach (var port in c.Ports) h.Add(port);
        }
        return h.ToHashCode();
    }

    private readonly SemaphoreSlim _reconciling = new(1, 1);
    private int _again;

    /// <summary>
    /// Plans afresh and pushes every config a host does not have yet. One plan at a time: a call while one runs makes
    /// it plan once more when done (the heartbeats of several hosts come in together).
    /// </summary>
    private async Task ReconcileAsync()
    {
        Volatile.Write(ref _again, 1);
        while (Volatile.Read(ref _again) == 1)
        {
            if (!await _reconciling.WaitAsync(0)) return; // the one running plans again
            try
            {
                while (Interlocked.Exchange(ref _again, 0) == 1)
                {
                    var snap = await FreshSnapshotAsync(CancellationToken.None);
                    // pushed side by side: one slow host does not hold up the others, nor the next plan
                    foreach (var (id, cfg) in snap.Plan.Configs)
                        if (snap.Online(id) && snap.StatusOf(id)?.ConfigHash != cfg.ConfigHash && Claim(id, cfg.ConfigHash))
                            _ = PushAsync(id, cfg, CancellationToken.None);
                }
            }
            catch (Exception ex) { _log.LogDebug(ex, "mesh reconcile"); }
            finally { _reconciling.Release(); }
        }
    }

    /// <summary>Whether to push this config now; claims the slot, so heartbeats meanwhile do not start the same push.</summary>
    private bool Claim(int agentId, string hash)
    {
        lock (_lastPush)
        {
            if (_lastPush.TryGetValue(agentId, out var last))
            {
                var since = DateTime.UtcNow - last.At;
                if (!last.Ok && since < FailedPushThrottle) return false;
                if (last.Ok && last.Hash == hash && since < SameConfigAgain) return false;
            }
            _lastPush[agentId] = (DateTime.UtcNow, true, hash);
            return true;
        }
    }
}
