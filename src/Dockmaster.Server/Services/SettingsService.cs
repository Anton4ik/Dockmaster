using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Server.Data;

namespace Dockmaster.Server.Services;

/// <summary>Key/value settings.</summary>
public sealed class SettingsService
{
    /// <summary>The one admin password from before users existed; only read by the move to the user "admin".</summary>
    public const string AdminPasswordKey = "admin.password";
    public const string AgentPublicUrlKey = "agent.public_url";
    public const string AgentImageKey = "agent.image";
    public const string DefaultAgentImage = "ghcr.io/anton4ik/dockmaster-agent:latest";
    public const string MeshSubnetKey = "mesh.subnet";
    public const string MeshPortKey = "mesh.port";
    /// <summary>The tunnel MTU for every host; empty or 0 = from the measured paths (Tune speed), else WireGuard's default.</summary>
    public const string MeshMtuKey = "mesh.mtu";
    /// <summary>"1": the mesh hosts get BBR and bigger TCP buffers (see dm-mesh tune_host).</summary>
    public const string MeshTcpTuningKey = "mesh.tcp_tuning";
    public const string DefaultMeshSubnet = "10.77.0.0/16";
    public const int DefaultMeshPort = 51821; // 51820 is what a WireGuard already on the host most likely uses
    /// <summary>Marks the one-off move of pre-network mesh members into a "default" network as done.</summary>
    public const string MeshNetworksMarkerKey = "mesh.networks_v1";
    /// <summary>The global hub from before networks existed; only read by that migration.</summary>
    public const string LegacyMeshHubKey = "mesh.hub";
    /// <summary>The agent port when DOCKMASTER_AGENTPORT is not set.</summary>
    public const int DefaultAgentPort = 9443;

    private readonly IDbContextFactory<DockmasterDb> _db;
    public SettingsService(IDbContextFactory<DockmasterDb> db) => _db = db;

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return (await db.Settings.FindAsync([key], ct))?.Value;
    }

    public Task SetAsync(string key, string value, CancellationToken ct = default) =>
        SetManyAsync(new Dictionary<string, string> { [key] = value }, ct);

    /// <summary>Stores several values in one save: all of them or none.</summary>
    public async Task SetManyAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        foreach (var (key, value) in values)
        {
            var e = await db.Settings.FindAsync([key], ct);
            if (e == null) db.Settings.Add(new SettingEntity { Key = key, Value = value });
            else e.Value = value;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<string> AgentImageAsync(CancellationToken ct = default) => await GetAsync(AgentImageKey, ct) is { Length: > 0 } image ? image : DefaultAgentImage;

    /// <param name="Mtu">a tunnel MTU set by hand for every host; 0 = each host's from its measured paths, else the default</param>
    /// <param name="TcpTuning">the hosts get BBR and bigger TCP buffers</param>
    public sealed record MeshSettings(string Subnet, int Port, string? ServerPublicHost, int Mtu = 0, bool TcpTuning = false);

    /// <summary>Mesh subnet, UDP port, MTU, TCP tuning and the host name agents reach the server at (hubs are chosen per network).</summary>
    public async Task<MeshSettings> MeshAsync(CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var all = await db.Settings.Where(s => s.Key.StartsWith("mesh.") || s.Key == AgentPublicUrlKey).ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var subnet = all.GetValueOrDefault(MeshSubnetKey);
        var port = int.TryParse(all.GetValueOrDefault(MeshPortKey), out var p) && p > 0 ? p : DefaultMeshPort;
        var mtu = int.TryParse(all.GetValueOrDefault(MeshMtuKey), out var m) && m > 0 ? m : 0;
        var host = Uri.TryCreate(all.GetValueOrDefault(AgentPublicUrlKey), UriKind.Absolute, out var uri) ? uri.Host : null;
        return new MeshSettings(string.IsNullOrWhiteSpace(subnet) ? DefaultMeshSubnet : subnet, port, host, mtu, all.GetValueOrDefault(MeshTcpTuningKey) == "1");
    }

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}
