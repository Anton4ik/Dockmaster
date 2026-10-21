using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Server.Access;
using Dockmaster.Server.Api;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Server.Hubs;

/// <summary>Pushes agent heartbeats to browsers through the SignalR hub.</summary>
public sealed class AgentBroadcaster : BackgroundService
{
    /// <summary>A stored agent and the order its load started in: a load that started later is never overwritten by an older one.</summary>
    private sealed record CachedAgent(AgentEntity Agent, long Version);

    private readonly AgentRegistry _agents;
    private readonly IHubContext<LiveHub> _hub;
    private readonly IDbContextFactory<DockmasterDb> _dbf;
    private readonly ImageUpdateChecker _updates;
    private readonly JobIndex _jobs;
    private readonly Watchers _watchers;
    private readonly ILogger<AgentBroadcaster> _log;
    private readonly AccessService _access;
    // every agent sends a heartbeat every few seconds: the stored part is read again only when it may have changed
    private readonly ConcurrentDictionary<int, CachedAgent> _stored = new();
    private long _loads;

    public AgentBroadcaster(AgentRegistry agents, IHubContext<LiveHub> hub, IDbContextFactory<DockmasterDb> dbf, ImageUpdateChecker updates, Watchers watchers,
        JobIndex jobs, AccessService access, ILogger<AgentBroadcaster> log)
    {
        _access = access;
        _jobs = jobs;
        _agents = agents;
        _hub = hub;
        _dbf = dbf;
        _updates = updates;
        _watchers = watchers;
        _log = log;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _agents.Changed += OnChanged;
        stoppingToken.Register(() => _agents.Changed -= OnChanged);
        return Task.CompletedTask;
    }

    private void OnChanged(int agentId, bool heartbeat)
    {
        // the stored part is read again after a change to it, also with no browser open: the next push must have it
        if (!heartbeat) _stored.TryRemove(agentId, out _);
        if (_watchers.Connections > 0) _ = PushAsync(agentId);
    }

    private async Task PushAsync(int agentId)
    {
        try
        {
            if (await StoredAsync(agentId) is not { } entity) return;
            var s = _agents.Get(agentId);
            // with the jobs, like GET /api/agents: a push that counted a waiting job as a stopped container made the
            // dashboard's numbers flip on every heartbeat
            // only to the users who see the server (the container lists go to the pages that watch it, checked when they asked)
            await _hub.Clients.Users(await _access.WhoSeesAsync(agentId)).SendAsync("agent", entity.ToDto(s, _updates, _jobs));
            // the container list is only built for the pages that show it
            if (s?.Last != null && _watchers.IsWatched(agentId))
                await _hub.Clients.Group(LiveHub.AgentGroup(agentId)).SendAsync("containers", agentId, s.Last.Containers.Select(c => c.ToDto(_updates, _jobs.Info(agentId, c))).ToArray());
        }
        catch (Exception ex) { _log.LogDebug(ex, "push agent {id}", agentId); }
    }

    /// <summary>The agent as stored, from the cache; null once it is deleted.</summary>
    private async Task<AgentEntity?> StoredAsync(int agentId)
    {
        if (_stored.TryGetValue(agentId, out var hit)) return hit.Agent;
        var version = Interlocked.Increment(ref _loads);
        await using var db = await _dbf.CreateDbContextAsync();
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId);
        if (agent == null)
        {
            _stored.TryRemove(agentId, out _);
            return null;
        }
        var loaded = new CachedAgent(agent, version);
        _stored.AddOrUpdate(agentId, loaded, (_, current) => current.Version > version ? current : loaded);
        return agent;
    }
}
