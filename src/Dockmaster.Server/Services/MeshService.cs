using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Protocol;
using Dockmaster.Server.Data;

namespace Dockmaster.Server.Services;

/// <summary>
/// Runs the mesh: joins and leaves, computes every host's WireGuard config (MeshPlanner) and pushes it to the agents
/// whose applied config (echoed in their heartbeat as a hash) differs. A reconcile on every agent change covers
/// reconnects, agent updates and hosts that were offline during a push.
/// </summary>
public sealed partial class MeshService : BackgroundService
{
    private static readonly TimeSpan PlanTtl = TimeSpan.FromSeconds(10);

    public sealed record Snapshot(SettingsService.MeshSettings Settings, List<AgentEntity> Agents, Dictionary<int, MeshNode> Nodes, List<MeshNetworkDef> Networks,
        MeshPlan Plan, Dictionary<int, MeshStatus?> Status)
    {
        public bool Online(int agentId) => Status.ContainsKey(agentId);
        public AgentEntity Agent(int agentId) => Agents.First(a => a.Id == agentId);
        /// <summary>What the agent reports about its mesh right now; null when offline or silent.</summary>
        public MeshStatus? StatusOf(int agentId) => Status.GetValueOrDefault(agentId);
    }

    private readonly IDbContextFactory<DockmasterDb> _dbf;
    private readonly AgentRegistry _agents;
    private readonly SettingsService _settings;
    private readonly Notifier _notifier;
    private readonly ILogger<MeshService> _log;
    private readonly ConcurrentDictionary<int, AgentSession> _unsupported = new();
    // agent id -> its key then and the public address its planned peers last saw its packets come from; kept across a
    // missed heartbeat, so a path does not flip between the LAN and the hub whenever one status is late
    private readonly ConcurrentDictionary<int, (string Key, string Host)> _seenFrom = new();
    // agents that said they apply plain routes; remembered while one is offline, so its peers' paths stay put
    private readonly ConcurrentDictionary<int, bool> _plainRoutes = new();
    private Snapshot? _cached;
    private DateTime _cachedAt;
    private readonly object _refreshLock = new();
    private Task<Snapshot>? _refresh;

    public MeshService(IDbContextFactory<DockmasterDb> dbf, AgentRegistry agents, SettingsService settings, Notifier notifier, ILogger<MeshService> log)
    {
        _dbf = dbf;
        _agents = agents;
        _settings = settings;
        _notifier = notifier;
        _log = log;
    }

    // a host that was offline during a push, or whose apply failed, gets the config on this timer
    private static readonly TimeSpan ReconcileEvery = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _agents.Changed += OnChanged;
        stoppingToken.Register(() => _agents.Changed -= OnChanged);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(ReconcileEvery, stoppingToken);
                // nothing to reconcile without hosts in the mesh: the last plan says so, and a join plans afresh
                if (_cached is { Nodes.Count: 0 }) continue;
                // one bad pass must not end the loop: an exception leaving a background service stops the whole server
                try
                {
                    await ReconcileAsync();
                    await WatchHandshakesAsync();
                }
                // a timeout inside a pass is an OperationCanceledException too: only the server stopping ends the loop
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { _log.LogError(ex, "mesh reconcile failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Hosts, the plan for them and what each agent reports right now; the plan may be up to a few seconds old.</summary>
    public Task<Snapshot> SnapshotAsync(CancellationToken ct)
    {
        // read once: a refresh on another thread may replace it meanwhile
        var cached = _cached;
        if (cached != null && DateTime.UtcNow - _cachedAt < PlanTtl) return Task.FromResult(cached with { Status = StatusNow(cached.Agents) });
        // one refresh at a time: every server's heartbeat asks, and they come in together
        lock (_refreshLock)
        {
            if (_refresh is not { IsCompleted: false }) _refresh = Task.Run(() => FreshSnapshotAsync(CancellationToken.None));
            return _refresh.WaitAsync(ct);
        }
    }

    /// <summary>A snapshot planned from what is stored now, e.g. right after a change.</summary>
    public async Task<Snapshot> FreshSnapshotAsync(CancellationToken ct)
    {
        var settings = await _settings.MeshAsync(ct);
        List<AgentEntity> agents;
        List<MeshNetworkDef> networks;
        List<ProjectEntity> projects;
        await using (var db = await _dbf.CreateDbContextAsync(ct))
        {
            agents = await db.Agents.OrderBy(a => a.Name).ToListAsync(ct);
            // only the projects that can have balancers (see MeshBalancers): the plan runs on every change a heartbeat brings
            projects = await db.Projects.Where(p => p.ComposeYaml.Contains("x-dockmaster") && p.ComposeYaml.Contains("expose")).ToListAsync(ct);
            var members = await db.MeshNetworkMembers.ToListAsync(ct);
            networks = (await db.MeshNetworks.OrderBy(n => n.Name).ToListAsync(ct))
                .Select(n => new MeshNetworkDef(n.Id, n.Name, n.HubAgentId, members.Where(m => m.NetworkId == n.Id).Select(m => m.AgentId).ToHashSet(), n.Trusted)).ToList();
        }
        NoteSeenFrom(agents, networks);
        var nodes = agents.Where(a => a.InMesh).Select(a =>
        {
            var s = _agents.Get(a.Id);
            var hostIps = s?.Last?.Mesh?.HostIps is { Count: > 0 } live ? live.ToList() : a.HostIps.ToList();
            return new MeshNode(a.Id, a.Name, a.MeshPublicKey!, a.MeshIp!, a.MeshPort ?? settings.Port,
                s?.PeerIp is { Length: > 0 } peer ? peer : a.PeerIp, hostIps, a.MeshPublic, a.MeshEndpoint, IsServerHost(s),
                _seenFrom.TryGetValue(a.Id, out var seen) && seen.Key == a.MeshPublicKey ? seen.Host : null, PlainRoutes(a.Id, s), a.MeshPathMtu ?? 0);
        }).ToList();
        // the balancers come from the projects' compose files: not parsed while no host is in the mesh
        var plan = MeshPlanner.Plan(nodes, networks, settings, nodes.Count == 0 ? new() : Balancers(agents, projects));
        var snap = new Snapshot(settings, agents, nodes.ToDictionary(n => n.AgentId), networks, plan, StatusNow(agents));
        _cached = snap;
        _cachedAt = DateTime.UtcNow;
        return snap;
    }

    private bool PlainRoutes(int agentId, AgentSession? s)
    {
        if (s?.Last?.Mesh is { } status) _plainRoutes[agentId] = status.PlainRoutes;
        return _plainRoutes.GetValueOrDefault(agentId);
    }

    /// <summary>The balancers for projects spread over servers, from what the heartbeats say runs where.</summary>
    private Dictionary<int, IReadOnlyList<MeshFrontend>> Balancers(List<AgentEntity> agents, List<ProjectEntity> projects)
    {
        var sessions = agents.ToDictionary(a => a.Id, a => _agents.Get(a.Id));
        return MeshBalancers.Plan(projects,
            agents.DistinctBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(a => a.Name, a => a.Id, StringComparer.OrdinalIgnoreCase),
            agents.ToDictionary(a => a.Id, a => a.Name),
            agents.ToDictionary(a => a.Id, a => a.InMesh ? a.MeshIp : null),
            sessions.Where(s => s.Value != null).Select(s => s.Key).ToHashSet(),
            sessions.ToDictionary(s => s.Key, s => (IReadOnlyList<ContainerInfo>)(s.Value?.Last?.Containers.ToList() ?? [])));
    }

    /// <summary>
    /// Where each host's WireGuard packets come from, as its peers report it: the public address of its NAT. A peer
    /// counts only when the plan gives it to the reporting host (before the first plan: a host it shares a network
    /// with), so an agent cannot point the mesh at an address of its choice for any key it likes.
    /// </summary>
    private void NoteSeenFrom(List<AgentEntity> agents, List<MeshNetworkDef> networks)
    {
        var configs = _cached?.Plan.Configs;
        var keyOf = agents.Where(a => a.InMesh).ToDictionary(a => a.Id, a => a.MeshPublicKey!);
        IReadOnlySet<string> Planned(int agentId) => configs != null
            ? configs.GetValueOrDefault(agentId)?.Peers.Select(p => p.PublicKey).ToHashSet() ?? []
            : networks.Where(n => n.Members.Contains(agentId)).SelectMany(n => n.Members).Where(id => id != agentId)
                .Select(id => keyOf.GetValueOrDefault(id)).OfType<string>().ToHashSet();
        foreach (var (id, key, host) in SeenFrom(agents, id => _agents.Get(id)?.Last?.Mesh?.Peers, Planned))
            _seenFrom[id] = (key, host);
        // servers deleted or out of the mesh
        foreach (var id in _seenFrom.Keys.Where(id => !keyOf.ContainsKey(id)).ToList()) _seenFrom.TryRemove(id, out _);
    }

    /// <summary>
    /// The public addresses hosts report their peers' packets from, by the peer's agent id: only for keys among the
    /// reporting host's <paramref name="planned"/> peers, and only for a key one host of the mesh has.
    /// </summary>
    public static IEnumerable<(int AgentId, string Key, string Host)> SeenFrom(IReadOnlyList<AgentEntity> agents,
        Func<int, IEnumerable<MeshPeerStatus>?> reported, Func<int, IReadOnlySet<string>> planned)
    {
        var byKey = agents.Where(a => a.InMesh).GroupBy(a => a.MeshPublicKey!).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Id);
        foreach (var a in agents.Where(a => a.InMesh))
        {
            if (reported(a.Id) is not { } peers) continue;
            var mine = planned(a.Id);
            foreach (var peer in peers)
                if (mine.Contains(peer.PublicKey) && byKey.TryGetValue(peer.PublicKey, out var id) && id != a.Id
                    && MeshPlanner.EndpointHost(peer.Endpoint) is { } host && MeshPlanner.IsPublicIp(host))
                    yield return (id, peer.PublicKey, host);
        }
    }

    private Dictionary<int, MeshStatus?> StatusNow(List<AgentEntity> agents) =>
        agents.Where(a => _agents.Get(a.Id) != null).ToDictionary(a => a.Id, a => _agents.Get(a.Id)?.Last?.Mesh);

    /// <summary>The Dockmaster server runs on this agent's host (agents reach it already, so its host is public by default).</summary>
    public static bool IsServerHost(AgentSession? s) =>
        s?.Last?.Containers.Any(c => c.State == "running" && c.ImageRef.Contains("dockmaster-server")) == true;

    /// <summary>
    /// Whether the agent understands mesh commands. Agents built before the mesh answer "unknown command"; that is
    /// remembered for the session, so the UI can say "update the agent" instead of offering the join again.
    /// </summary>
    public bool Supported(int agentId) => !(_unsupported.TryGetValue(agentId, out var s) && ReferenceEquals(s, _agents.Get(agentId)));

    private void NoteUnsupported(int agentId, CommandResult r)
    {
        if (r.Message == WireText.UnknownCommand && _agents.Get(agentId) is { } s) _unsupported[agentId] = s;
    }
}
