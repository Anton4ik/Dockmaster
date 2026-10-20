using System.Collections.Concurrent;

namespace Dockmaster.Server.Services;

/// <summary>
/// Who is looking at what right now: the browsers connected to the hub, the servers whose container lists they
/// watch and whether the Network page is open. What nobody looks at is reported less often (<see cref="Pacer"/>)
/// and not pushed at all (<see cref="Hubs.AgentBroadcaster"/>).
/// </summary>
public sealed class Watchers
{
    /// <summary>The Network page polls the mesh every few seconds: it counts as open this long after its last poll.</summary>
    private static readonly TimeSpan MeshOpenFor = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, HashSet<int>> _byConnection = new();
    private readonly ConcurrentDictionary<int, int> _watching = new();
    // connections whose tab is in the background (minimized, another tab in front)
    private readonly ConcurrentDictionary<string, bool> _hidden = new();
    private readonly object _lock = new();
    private int _connections;
    private long _meshSeenTicks;

    /// <summary>After a browser connected, left, or started or stopped watching a server.</summary>
    public event Action? Changed;

    /// <summary>Browsers connected to the hub.</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>Connected browsers whose tab is in front: a tab left open in the background does not count.</summary>
    public int Visible => Math.Max(0, Connections - _hidden.Count);

    /// <summary>Whether a browser shows this server's containers (its page, a container of it, a project on it).</summary>
    public bool IsWatched(int agentId) => _watching.GetValueOrDefault(agentId) > 0;

    /// <summary>Whether the Network page is open somewhere.</summary>
    public bool MeshWatched => DateTime.UtcNow.Ticks - Volatile.Read(ref _meshSeenTicks) < MeshOpenFor.Ticks;

    public void Connected(string connectionId)
    {
        _byConnection[connectionId] = [];
        Interlocked.Increment(ref _connections);
        Changed?.Invoke();
    }

    public void Disconnected(string connectionId)
    {
        if (!_byConnection.TryRemove(connectionId, out var agents)) return;
        _hidden.TryRemove(connectionId, out _);
        Interlocked.Decrement(ref _connections);
        lock (_lock)
            foreach (var id in agents) Release(id);
        Changed?.Invoke();
    }

    public void Watch(string connectionId, int agentId)
    {
        if (!_byConnection.TryGetValue(connectionId, out var agents)) return;
        lock (_lock)
        {
            if (!agents.Add(agentId)) return;
            _watching.AddOrUpdate(agentId, 1, (_, n) => n + 1);
        }
        Changed?.Invoke();
    }

    public void Unwatch(string connectionId, int agentId)
    {
        if (!_byConnection.TryGetValue(connectionId, out var agents)) return;
        lock (_lock)
        {
            if (!agents.Remove(agentId)) return;
            Release(agentId);
        }
        Changed?.Invoke();
    }

    /// <summary>The tab went to the background or came back.</summary>
    public void SetHidden(string connectionId, bool hidden)
    {
        if (!_byConnection.ContainsKey(connectionId)) return;
        var changed = hidden ? _hidden.TryAdd(connectionId, true) : _hidden.TryRemove(connectionId, out _);
        if (changed) Changed?.Invoke();
    }

    /// <summary>The Network page asked for the mesh.</summary>
    public void MeshSeen()
    {
        var was = MeshWatched;
        Volatile.Write(ref _meshSeenTicks, DateTime.UtcNow.Ticks);
        if (!was) Changed?.Invoke();
    }

    private void Release(int agentId)
    {
        if (_watching.AddOrUpdate(agentId, 0, (_, n) => n - 1) <= 0) _watching.TryRemove(agentId, out _);
    }
}
