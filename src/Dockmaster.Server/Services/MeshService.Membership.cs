using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Protocol;
using Dockmaster.Server.Data;

namespace Dockmaster.Server.Services;

/// <summary>
/// Servers joining and leaving the mesh and its networks. Joining and leaving wait for the agent for a while: no
/// database context is held open around those calls.
/// </summary>
public sealed partial class MeshService
{
    public const int MinPort = 1024;
    public const int MaxPort = 65535;
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan LeaveTimeout = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record JoinPayload(string PublicKey, string Backend, int ListenPort, List<string> HostIps, List<string> Problems);

    public static string? ValidatePort(int port) =>
        port is < MinPort or > MaxPort ? $"mesh port must be between {MinPort} and {MaxPort}" : null;

    /// <summary>
    /// Puts a server into a network. A server not in the mesh yet joins it first (keys, sidecar, address); without a
    /// network given it goes into the only one there is, or a new "default" one.
    /// </summary>
    public async Task<(bool ok, string message)> AddToNetworkAsync(int agentId, int? networkId, Action<string> onLine, CancellationToken ct)
    {
        MeshNetworkEntity network;
        AgentEntity agent;
        await using (var db = await _dbf.CreateDbContextAsync(ct))
        {
            if (networkId is { } nid)
                network = await db.MeshNetworks.FindAsync([nid], ct) ?? throw new InvalidOperationException("network not found");
            else
            {
                var all = await db.MeshNetworks.ToListAsync(ct);
                network = all.Count == 1 ? all[0] : all.FirstOrDefault(n => n.Name == "default") ?? new MeshNetworkEntity { Name = "default" };
                if (network.Id == 0) { db.MeshNetworks.Add(network); await db.SaveChangesAsync(ct); }
            }
            agent = await db.Agents.FindAsync([agentId], ct) ?? throw new InvalidOperationException("server not found");
        }
        if (!agent.InMesh)
        {
            var (ok, message) = await JoinAsync(agent, onLine, ct);
            if (!ok) return (false, message);
        }
        await using (var db = await _dbf.CreateDbContextAsync(ct))
        {
            if (!await db.MeshNetworkMembers.AnyAsync(m => m.NetworkId == network.Id && m.AgentId == agentId, ct))
            {
                db.MeshNetworkMembers.Add(new MeshNetworkMemberEntity { NetworkId = network.Id, AgentId = agentId });
                await db.SaveChangesAsync(ct);
            }
        }
        onLine($"{agent.Name} is in network {network.Name}");
        _agents.Notify(agentId);
        await PushAllAsync(onLine, ct);
        return (true, $"in network {network.Name}");
    }

    /// <summary>Out of one network; a server left in no network keeps its mesh address and simply has no peers.</summary>
    public async Task<string?> RemoveFromNetworkAsync(int networkId, int agentId, CancellationToken ct)
    {
        await using (var db = await _dbf.CreateDbContextAsync(ct))
            if (await DropMembershipsAsync(db, agentId, networkId, ct) == 0) return "the server is not in that network";
        _agents.Notify(agentId);
        await PushAllAsync(null, ct);
        return null;
    }

    /// <summary>Keys, sidecar and an address for a server; the caller pushes the new configs.</summary>
    private async Task<(bool ok, string message)> JoinAsync(AgentEntity agent, Action<string> onLine, CancellationToken ct)
    {
        var agentId = agent.Id;
        if (_agents.Get(agentId) == null) return (false, AgentRegistry.OfflineMessage);
        var settings = await _settings.MeshAsync(ct);
        var join = new MeshJoin { ListenPort = agent.MeshPort ?? settings.Port, Subnet = settings.Subnet };
        onLine($"preparing {agent.Name}: keys, sidecar, host checks");
        var r = await _agents.SendAsync(agentId, new Command { MeshJoin = join }, JoinTimeout, ct, onLine);
        if (!r.Ok) { NoteUnsupported(agentId, r); return (false, AgentRegistry.ErrorText(r)); }
        JoinPayload payload;
        try { payload = JsonSerializer.Deserialize<JoinPayload>(r.Payload, Json) ?? throw new InvalidOperationException("empty answer"); }
        catch (Exception ex) { return (false, "unreadable answer from the agent: " + ex.Message); }
        if (payload.Problems.Count > 0) return (false, string.Join("; ", payload.Problems));
        // it goes into the config of every other host as it is
        if (!MeshPlanner.IsWgKey(payload.PublicKey)) return (false, "the agent answered with a malformed public key");

        string ip;
        await using (var db = await _dbf.CreateDbContextAsync(ct))
        {
            var a = await db.Agents.FindAsync([agentId], ct) ?? throw new InvalidOperationException("server not found");
            // a cloned host brings the key of the one it was cloned from: two peers with one key break both tunnels
            if (await db.Agents.Where(x => x.Id != agentId && x.MeshPublicKey == payload.PublicKey).Select(x => x.Name).FirstOrDefaultAsync(ct) is { } twin)
                return (false, $"{agent.Name} has the same WireGuard key as {twin} (a cloned host?): delete private.key and public.key in the agent's mesh folder and join again");
            var used =await db.Agents.Where(x => x.MeshIp != null && x.Id != agentId).Select(x => x.MeshIp!).ToListAsync(ct);
            // a re-join keeps the address (other hosts may have it in extra_hosts already)
            ip = a.MeshIp != null && SubnetAllocator.Contains(settings.Subnet, a.MeshIp) ? a.MeshIp : SubnetAllocator.Next(settings.Subnet, used);
            a.MeshIp = ip;
            a.MeshPublicKey = payload.PublicKey;
            a.MeshPort = payload.ListenPort;
            a.MeshJoinedAt ??= DateTime.UtcNow;
            a.MeshHostIps = string.Join(",", payload.HostIps);
            await db.SaveChangesAsync(ct);
        }
        onLine($"{agent.Name} is {ip} ({payload.Backend}, UDP {payload.ListenPort})");
        _agents.Notify(agentId);
        return (true, $"joined as {ip}");
    }

    public async Task<(bool ok, string message)> LeaveAsync(int agentId, Action<string> onLine, CancellationToken ct)
    {
        AgentEntity agent;
        await using (var db = await _dbf.CreateDbContextAsync(ct))
            agent = await db.Agents.FindAsync([agentId], ct) ?? throw new InvalidOperationException("server not found");
        if (!agent.InMesh) return (true, "not in the mesh");
        if (_agents.Get(agentId) != null)
        {
            var r = await _agents.SendAsync(agentId, new Command { MeshLeave = new MeshLeave() }, LeaveTimeout, ct, onLine);
            onLine(r.Ok ? $"{agent.Name}: {r.Message}" : $"{agent.Name}: could not clean up ({AgentRegistry.ErrorText(r)}) — removed from the mesh anyway");
        }
        else onLine($"{agent.Name} is offline: run `docker rm -f dockmaster-mesh` there when it is back");
        await using (var db = await _dbf.CreateDbContextAsync(ct))
        {
            await db.Agents.Where(a => a.Id == agentId).ExecuteUpdateAsync(s => s
                .SetProperty(a => a.MeshIp, (string?)null)
                .SetProperty(a => a.MeshPublicKey, (string?)null)
                .SetProperty(a => a.MeshJoinedAt, (DateTime?)null)
                .SetProperty(a => a.MeshHostIps, (string?)null), ct);
            await DropMembershipsAsync(db, agentId, null, ct);
        }
        _agents.Notify(agentId);
        await PushAllAsync(onLine, ct);
        return (true, "left the mesh");
    }

    /// <summary>Public flag / endpoint / port the user set for one host; the others get their configs updated.</summary>
    public async Task<string?> ConfigureAsync(int agentId, bool? isPublic, string? endpoint, int? port, CancellationToken ct)
    {
        endpoint = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim();
        if (endpoint != null && !MeshPlanner.IsEndpointText(endpoint)) return "endpoint is host or host:port";
        if (port is { } p && ValidatePort(p) is { } err) return err;
        await using (var db = await _dbf.CreateDbContextAsync(ct))
        {
            var a = await db.Agents.FindAsync([agentId], ct);
            if (a == null) return "server not found";
            a.MeshPublic = isPublic;
            a.MeshEndpoint = endpoint;
            if (port != null) a.MeshPort = port;
            await db.SaveChangesAsync(ct);
        }
        _agents.Notify(agentId);
        await PushAllAsync(null, ct);
        return null;
    }

    /// <summary>Takes a server out of one network (or of all, null) and clears it as the hub chosen there; returns how many memberships went.</summary>
    private static async Task<int> DropMembershipsAsync(DockmasterDb db, int agentId, int? networkId, CancellationToken ct)
    {
        var members = db.MeshNetworkMembers.Where(m => m.AgentId == agentId);
        var hubOf = db.MeshNetworks.Where(n => n.HubAgentId == agentId);
        if (networkId is { } id)
        {
            members = members.Where(m => m.NetworkId == id);
            hubOf = hubOf.Where(n => n.Id == id);
        }
        var removed = await members.ExecuteDeleteAsync(ct);
        await hubOf.ExecuteUpdateAsync(s => s.SetProperty(n => n.HubAgentId, (int?)null), ct);
        return removed;
    }
}
