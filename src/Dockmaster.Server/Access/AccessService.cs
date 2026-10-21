using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Server.Access;

/// <summary>What one signed-in user may do: everything (an admin), or some permissions on some servers.</summary>
public sealed class UserAccess(int userId, string name, bool isAdmin, IReadOnlySet<string> everywhere, IReadOnlyDictionary<int, IReadOnlySet<string>> byServer)
{
    public int UserId { get; } = userId;
    public string Name { get; } = name;
    public bool IsAdmin { get; } = isAdmin;
    /// <summary>Permissions on every server, also ones added later.</summary>
    public IReadOnlySet<string> Everywhere { get; } = everywhere;
    /// <summary>Permissions on single servers (on top of <see cref="Everywhere"/>).</summary>
    public IReadOnlyDictionary<int, IReadOnlySet<string>> ByServer { get; } = byServer;

    public bool Can(int agentId, string perm) => IsAdmin || Everywhere.Contains(perm) || ByServer.TryGetValue(agentId, out var p) && p.Contains(perm);

    public bool CanAll(IEnumerable<int> agentIds, string perm) => agentIds.All(id => Can(id, perm));

    public bool Sees(int agentId) => Can(agentId, Perm.View);

    /// <summary>Everything the user may do on the server, in the canonical order; empty = the server is not theirs to see.</summary>
    public IReadOnlyList<string> On(int agentId) =>
        IsAdmin ? [.. Perm.All.Select(p => p.Key)]
        : Everywhere.Count == 0 && !ByServer.ContainsKey(agentId) ? []
        : Perm.Normalize(Everywhere.Concat(ByServer.GetValueOrDefault(agentId) ?? new HashSet<string>()));
}

/// <summary>
/// Who may do what, read from the users, roles and grants and kept in memory until one of them changes. Every request
/// and hub call asks here, so a change applies at once: to the REST API with the next request, to an open hub
/// connection by ending it (the browser connects again and is checked anew).
/// </summary>
public sealed class AccessService(IDbContextFactory<DockmasterDb> dbf)
{
    /// <summary>The cookie carries the user's session stamp: a new password or a disabled account ends the sessions.</summary>
    public const string StampClaim = "dm:stamp";

    private sealed record Snapshot(long Version, IReadOnlyDictionary<int, UserEntity> Users, IReadOnlyDictionary<int, UserAccess> Access);

    private readonly SemaphoreSlim _loading = new(1, 1);
    private volatile Snapshot? _snapshot;
    private long _version;
    // hub connections, by connection id: the ones of a user whose access changed are ended
    private readonly ConcurrentDictionary<string, (int UserId, HubCallerContext Context)> _connections = new();

    /// <summary>The signed-in user's access; null when nobody is signed in, or the account is gone, disabled, or signed in with an old password.</summary>
    public async Task<UserAccess?> ForAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        if (principal.Identity?.IsAuthenticated != true || !int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return null;
        var s = await SnapshotAsync(ct);
        return s.Users.TryGetValue(id, out var u) && !u.Disabled && u.SessionStamp == principal.FindFirstValue(StampClaim) ? s.Access[id] : null;
    }

    /// <summary>The ids (as the hub knows users) of the enabled users who see the server; null = only the admins.</summary>
    public async Task<IReadOnlyList<string>> WhoSeesAsync(int? agentId, CancellationToken ct = default)
    {
        var s = await SnapshotAsync(ct);
        return s.Access.Values.Where(a => !s.Users[a.UserId].Disabled && (a.IsAdmin || agentId is { } id && a.Sees(id))).Select(a => a.UserId.ToString()).ToList();
    }

    /// <summary>Users, roles or grants were changed: read again, and the hub connections of these users end.</summary>
    public void Changed(IEnumerable<int> users)
    {
        Interlocked.Increment(ref _version);
        _snapshot = null;
        var ended = users.ToHashSet();
        foreach (var (_, c) in _connections.Where(c => ended.Contains(c.Value.UserId))) c.Context.Abort();
    }

    public void Connected(int userId, HubCallerContext context) => _connections[context.ConnectionId] = (userId, context);

    public void Disconnected(string connectionId) => _connections.TryRemove(connectionId, out _);

    /// <summary>
    /// Why the user may not do <paramref name="perms"/> with the project, or null when they may. Seeing it takes View on
    /// its own server; anything else takes the permissions on every server it is placed, scaled or deployed on.
    /// </summary>
    public async Task<(bool Found, string? Denied)> ProjectDeniedAsync(UserAccess a, int projectId, IReadOnlyCollection<string> perms, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        if (await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct) is not { } p) return (false, null);
        if (a.IsAdmin) return (true, null);
        // a project of a server the user does not see is not there for them
        if (!a.Sees(p.AgentId)) return (false, null);
        if (perms.All(x => x == Perm.View)) return (true, null);
        var servers = ProjectServers(p, await ProjectService.AgentIdsByNameAsync(db, ct));
        return (true, Denied(a, servers, perms, await db.AgentNamesAsync(ct)));
    }

    /// <summary>
    /// Why the user may not roll the project back to this deployment, or null. The compose file of then may place
    /// services on other servers than now's: deploy and secrets are needed on those too.
    /// </summary>
    public async Task<string?> RollbackDeniedAsync(UserAccess a, int projectId, int deploymentId, CancellationToken ct = default)
    {
        if (a.IsAdmin) return null;
        await using var db = await dbf.CreateDbContextAsync(ct);
        var compose = await db.Deployments.Where(d => d.Id == deploymentId && d.ProjectId == projectId).Select(d => d.ComposeYaml).FirstOrDefaultAsync(ct);
        // no files kept, or no project: the rollback itself says so
        if (compose is not { Length: > 0 } || await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct) is not { } p) return null;
        p.ComposeYaml = compose;
        var servers = ProjectServers(p, await ProjectService.AgentIdsByNameAsync(db, ct));
        return Denied(a, servers, [Perm.Deploy, Perm.Secrets], await db.AgentNamesAsync(ct));
    }

    /// <summary>"you may not deploy on nas" for the first permission missing on one of the servers, or null.</summary>
    public static string? Denied(UserAccess a, IEnumerable<int> servers, IEnumerable<string> perms, IReadOnlyDictionary<int, string> names)
    {
        foreach (var perm in perms)
            foreach (var id in servers)
                if (!a.Can(id, perm)) return $"you have no {Label(perm)} permission on {names.GetValueOrDefault(id, $"server #{id}")}";
        return null;
    }

    public static string Label(string perm) => Perm.All.FirstOrDefault(p => p.Key == perm)?.Label.ToLowerInvariant() ?? perm;

    /// <summary>Every server a project touches: its own, the ones services are placed or autoscaled on, the ones it was deployed to.</summary>
    public static IReadOnlyList<int> ProjectServers(ProjectEntity p, IReadOnlyDictionary<string, int> agentIdsByName)
    {
        var services = ComposeServices.Parse(p.ComposeYaml);
        var ids = ProjectService.AgentIdsOf(p, services, agentIdsByName);
        foreach (var name in services.SelectMany(s => s.Autoscale?.AgentNames ?? []))
            if (agentIdsByName.TryGetValue(name, out var id)) ids.Add(id);
        return ids.Concat(p.DeployedIds).Concat(p.Extras.Values.SelectMany(x => x.Keys)).Distinct().ToList();
    }

    private async Task<Snapshot> SnapshotAsync(CancellationToken ct)
    {
        var version = Interlocked.Read(ref _version);
        if (_snapshot is { } s && s.Version == version) return s;
        await _loading.WaitAsync(ct);
        try
        {
            version = Interlocked.Read(ref _version);
            if (_snapshot is { } again && again.Version == version) return again;
            var loaded = await LoadAsync(version, ct);
            // a change while it loaded: the next call reads again
            if (Interlocked.Read(ref _version) == version) _snapshot = loaded;
            return loaded;
        }
        finally { _loading.Release(); }
    }

    private async Task<Snapshot> LoadAsync(long version, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, ct);
        var roles = (await db.Roles.AsNoTracking().ToListAsync(ct)).ToDictionary(r => r.Id, r => r.PermissionList);
        var grants = (await db.UserGrants.AsNoTracking().ToListAsync(ct)).ToLookup(g => g.UserId);
        var access = users.Values.ToDictionary(u => u.Id, u =>
        {
            var mine = grants[u.Id].Where(g => roles.ContainsKey(g.RoleId)).ToList();
            var everywhere = mine.Where(g => g.AgentId == UserGrantEntity.AllServers).SelectMany(g => roles[g.RoleId]).ToHashSet();
            var byServer = mine.Where(g => g.AgentId != UserGrantEntity.AllServers)
                .ToDictionary(g => g.AgentId, g => (IReadOnlySet<string>)roles[g.RoleId].ToHashSet());
            return new UserAccess(u.Id, u.Name, u.IsAdmin, everywhere, byServer);
        });
        return new Snapshot(version, users, access);
    }
}
