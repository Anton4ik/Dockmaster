using System.Runtime.CompilerServices;
using Dockmaster.Protocol;

namespace Dockmaster.Server.Services;

/// <summary>
/// Tells each agent how often to report. Every heartbeat costs the host two docker API calls per container, and the
/// mesh status a docker exec with a ping per peer: a server nobody looks at reports every half minute (its minutes
/// are averaged from whatever comes in), one shown on a page every few seconds. A change is sent as soon as a page
/// opens or closes, and the Network page's polls keep the mesh status fresh while it is open.
/// </summary>
public sealed class Pacer : BackgroundService
{
    /// <summary>A server whose containers are on screen.</summary>
    public const int WatchedSeconds = 5;
    /// <summary>A browser is open in front (the dashboard and the menu show every server's state) but not on this server.</summary>
    public const int ConnectedSeconds = 20;
    /// <summary>Nobody is looking.</summary>
    public const int IdleSeconds = 30;
    public const int MeshWatchedSeconds = 5;
    public const int MeshIdleSeconds = 60;
    // the Network page stops polling without a goodbye: looked at this often
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    // a slower pace waits this long: a page reload leaves and comes back within a second, and a faster pace is
    // sent at once anyway. Sent from the next tick after the wait
    private static readonly TimeSpan SlowDownAfter = TimeSpan.FromSeconds(3);

    private readonly AgentRegistry _agents;
    private readonly Watchers _watchers;
    private readonly ILogger<Pacer> _log;
    // what each session was told last; a session that reconnects is a new object and starts over
    private readonly ConditionalWeakTable<AgentSession, Pace> _sent = new();
    // a slower pace a session is about to get, and since when it is wanted
    private readonly ConditionalWeakTable<AgentSession, Slower> _slower = new();
    private sealed record Slower(Pace Pace, DateTime Since);
    private readonly SemaphoreSlim _wake = new(0, 1);

    public Pacer(AgentRegistry agents, Watchers watchers, ILogger<Pacer> log)
    {
        _agents = agents;
        _watchers = watchers;
        _log = log;
    }

    /// <summary>
    /// The pace a server gets right now. While a command runs on it (a deploy, a re-create) it reports every few
    /// seconds too: the server follows its containers through the heartbeats, and the mesh balancers on the other
    /// servers learn of the new replicas from them.
    /// </summary>
    public Pace For(int agentId) => new()
    {
        HeartbeatSeconds = _watchers.IsWatched(agentId) || _agents.Get(agentId)?.Pending.IsEmpty == false ? WatchedSeconds
            : _watchers.Visible > 0 ? ConnectedSeconds : IdleSeconds,
        MeshSeconds = _watchers.MeshWatched ? MeshWatchedSeconds : MeshIdleSeconds,
    };

    /// <summary>What a session was told last (its Welcome, then the paces sent since).</summary>
    public void Told(AgentSession s, Pace pace) => _sent.AddOrUpdate(s, pace);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // called from other services' events (an agent registering): must never throw into them
        void Wake()
        {
            try { _wake.Release(); }
            catch (SemaphoreFullException) { } // woken already
        }
        void OnAgent(int agentId, bool heartbeat) { if (!heartbeat) Wake(); }
        _watchers.Changed += Wake;
        _agents.Changed += OnAgent;
        stoppingToken.Register(() => { _watchers.Changed -= Wake; _agents.Changed -= OnAgent; });
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _wake.WaitAsync(Tick, stoppingToken);
                await SendChangesAsync();
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task SendChangesAsync()
    {
        foreach (var s in _agents.All)
        {
            // a session is registered after its welcome, which says what it was told: none, nothing to send yet
            if (!_sent.TryGetValue(s, out var last)) continue;
            var pace = For(s.AgentId);
            if (last.HeartbeatSeconds == pace.HeartbeatSeconds && last.MeshSeconds == pace.MeshSeconds)
            {
                _slower.Remove(s);
                continue;
            }
            if (pace.HeartbeatSeconds >= last.HeartbeatSeconds && pace.MeshSeconds >= last.MeshSeconds)
            {
                // nothing got faster: wait to see whether the pace stays wanted
                if (!_slower.TryGetValue(s, out var wanted) || wanted.Pace.HeartbeatSeconds != pace.HeartbeatSeconds || wanted.Pace.MeshSeconds != pace.MeshSeconds)
                {
                    _slower.AddOrUpdate(s, new Slower(pace, DateTime.UtcNow));
                    continue;
                }
                if (DateTime.UtcNow - wanted.Since < SlowDownAfter) continue;
            }
            _slower.Remove(s);
            _sent.AddOrUpdate(s, pace);
            try { await s.Send(new ServerMessage { Pace = pace }); }
            catch (Exception ex) when (!s.Cts.IsCancellationRequested) { _log.LogDebug(ex, "pace for {agent} not sent", s.Name); }
            catch (OperationCanceledException) { }
        }
    }
}
