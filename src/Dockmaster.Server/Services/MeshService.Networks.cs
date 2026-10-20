using Microsoft.EntityFrameworkCore;
using Dockmaster.Server.Data;

namespace Dockmaster.Server.Services;

/// <summary>Networks: isolated groups of servers in the mesh.</summary>
public sealed partial class MeshService
{
    public static string? ValidateNetworkName(string name) =>
        name.Length is 0 or > 40 || name.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' or '.'))
            ? "network name: up to 40 letters, digits, spaces, '-', '_' or '.'" : null;

    public async Task<(MeshNetworkEntity? network, string? error)> CreateNetworkAsync(string name, CancellationToken ct)
    {
        name = name.Trim();
        if (ValidateNetworkName(name) is { } err) return (null, err);
        await using var db = await _dbf.CreateDbContextAsync(ct);
        if (await db.MeshNetworks.AnyAsync(n => n.Name == name, ct)) return (null, NameTaken);
        var n = new MeshNetworkEntity { Name = name };
        db.MeshNetworks.Add(n);
        await db.SaveChangesAsync(ct);
        return (n, null);
    }

    /// <summary>Rename, the hub the user picked (null = automatic), and whether its LANs are trusted (null = unchanged).</summary>
    public async Task<string?> UpdateNetworkAsync(int networkId, string name, int? hubAgentId, bool? trusted, CancellationToken ct)
    {
        name = name.Trim();
        if (ValidateNetworkName(name) is { } err) return err;
        await using (var db = await _dbf.CreateDbContextAsync(ct))
        {
            var n = await db.MeshNetworks.FindAsync([networkId], ct);
            if (n == null) return "network not found";
            if (n.Name != name && await db.MeshNetworks.AnyAsync(x => x.Name == name, ct)) return NameTaken;
            if (hubAgentId is { } hub && !await db.MeshNetworkMembers.AnyAsync(m => m.NetworkId == networkId && m.AgentId == hub, ct)) return "the hub must be a member of the network";
            n.Name = name;
            n.HubAgentId = hubAgentId;
            if (trusted is { } t) n.Trusted = t;
            await db.SaveChangesAsync(ct);
        }
        await PushAllAsync(null, ct);
        return null;
    }

    public async Task<string?> DeleteNetworkAsync(int networkId, CancellationToken ct)
    {
        await using (var db = await _dbf.CreateDbContextAsync(ct))
        {
            await db.MeshNetworkMembers.Where(m => m.NetworkId == networkId).ExecuteDeleteAsync(ct);
            if (await db.MeshNetworks.Where(n => n.Id == networkId).ExecuteDeleteAsync(ct) == 0) return "network not found";
        }
        await PushAllAsync(null, ct);
        return null;
    }

    private const string NameTaken = "a network with this name already exists";
}
