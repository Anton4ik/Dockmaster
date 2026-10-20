namespace Dockmaster.Server.Api;

// mesh: the WireGuard network between servers

/// <param name="Hints">what would make the mesh faster (nothing is wrong)</param>
/// <param name="Mtu">the tunnel MTU set by hand for every host in Settings; 0 = each host's from its measured paths</param>
/// <param name="TcpTuning">the hosts get BBR and bigger TCP buffers</param>
public sealed record MeshDto(string Subnet, int Port, MeshNetworkDto[] Networks, MeshNodeDto[] Nodes, MeshPairDto[] Pairs, string[] Problems,
    string[] Hints, int Mtu, bool TcpTuning);
/// <param name="HubAgentId">the hub in use (null = none: members behind different NATs cannot reach each other)</param>
/// <param name="HubOverride">the hub the user chose, null = automatic</param>
public sealed record MeshNetworkDto(int Id, string Name, int? HubAgentId, int? HubOverride, int[] Members, bool Trusted);
/// <param name="Public">reachable from the internet (decided from the addresses unless PublicOverride says otherwise)</param>
/// <param name="Hub">relays for at least one of its networks</param>
/// <param name="InSync">the config the agent runs is the planned one</param>
/// <param name="Supported">the agent understands mesh commands (older agents do not)</param>
/// <param name="Mtu">the tunnel MTU its config has</param>
/// <param name="PathMtu">the smallest packet its paths carry, as measured by Tune speed; 0 = never measured</param>
/// <param name="Notes">worth knowing, nothing to fix (userspace WireGuard on a host without the module, no BBR in the kernel)</param>
/// <param name="TcpCongestion">the host's TCP congestion control ("bbr", "cubic"); "" = not reported (older sidecar)</param>
public sealed record MeshNodeDto(int AgentId, string Name, bool Online, bool Joined, string? MeshIp, bool Public, bool? PublicOverride, string? Endpoint, string? EndpointOverride,
    int Port, string Backend, bool Hub, bool ServerHost, bool Up, bool InSync, string[] Problems, bool Supported, string[] HostIps, string? PeerIp, int[] NetworkIds,
    int Mtu = 0, int PathMtu = 0, string[]? Notes = null, string TcpCongestion = "")
{
    public string[] Notes { get; } = Notes ?? [];
}
/// <summary>One direction between two servers that share a network.</summary>
/// <param name="Path">direct | lan | via | none</param>
/// <param name="HandshakeAgeS">seconds since the last WireGuard handshake (through the hub: with the hub), null = never</param>
/// <param name="Rx">bytes received from the peer (through the hub: all of the hub's traffic, so 0 here)</param>
public sealed record MeshPairDto(int From, int To, string Path, string? ViaName, double? RttMs, long? HandshakeAgeS, long Rx, long Tx, int NetworkId);
public sealed record MeshAgentRequest(bool? Public, string? Endpoint, int? Port);
/// <param name="Trusted">null leaves it as it is</param>
public sealed record MeshNetworkRequest(string Name, int? HubAgentId = null, bool? Trusted = null);

/// <summary>A project that spans servers: which service on which server is used from where.</summary>
public sealed record ConnectionDto(int ProjectId, string Name, string[] Hosts, LinkDto[] Links, bool Problems);
public sealed record LinkDto(string Service, string FromHost, string ToHost, string[] Ports, string Address);
