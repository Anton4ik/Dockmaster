using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Dockmaster.Protocol;

namespace Dockmaster.Server.Services;

/// <summary>What the planner knows about one host in the mesh.</summary>
/// <param name="PeerIp">address its agent connection comes from: the host's own public address, or its NAT's</param>
/// <param name="HostIps">IPv4 addresses on the host itself</param>
/// <param name="PublicOverride">the user's word on reachability, null = decide from the addresses</param>
/// <param name="EndpointOverride">host or host:port the user typed</param>
/// <param name="ServerHost">the Dockmaster server runs on this host: when it is public, its endpoint is the host of the agent URL and it is the preferred hub</param>
/// <param name="SeenFrom">
/// the public address this host's WireGuard packets arrive from, as another host of the mesh sees them: its NAT's
/// address. Unlike <paramref name="PeerIp"/> it does not depend on how the agents reach the server (a server behind a
/// reverse proxy or Docker's NAT sees every agent coming from the same private address).
/// </param>
/// <param name="PlainRoutes">its agent can reach peers by plain routes (<see cref="PathKind.Plain"/>); older agents only tunnel</param>
/// <param name="PathMtu">the smallest IP packet the paths to its peers carry, as measured (Tune speed); 0 = not measured</param>
public sealed record MeshNode(int AgentId, string Name, string PublicKey, string MeshIp, int ListenPort,
    string? PeerIp, IReadOnlyList<string> HostIps, bool? PublicOverride, string? EndpointOverride, bool ServerHost, string? SeenFrom = null,
    bool PlainRoutes = false, int PathMtu = 0);

/// <summary>An isolated group of hosts: only hosts that share a network become peers.</summary>
/// <param name="HubOverride">the hub the user chose; used when that host is a public member</param>
/// <param name="Trusted">the user trusts the LANs of this network: members on one LAN talk without the tunnel, unencrypted</param>
public sealed record MeshNetworkDef(int Id, string Name, int? HubOverride, IReadOnlySet<int> Members, bool Trusted = false);

/// <summary>
/// Direct: a tunnel straight to the peer. Lan: a tunnel over the shared LAN. Plain: the shared LAN without a tunnel
/// (a trusted network). ViaHub: through the network's hub. None: no way.
/// </summary>
public enum PathKind { Direct, Lan, Plain, ViaHub, None }

/// <summary>How traffic from one host reaches another, within the network they share.</summary>
public sealed record MeshPath(int From, int To, PathKind Kind, int? HubId, int NetworkId);

/// <param name="Hubs">network id → the hub that forwards between its members behind different NATs (null = none)</param>
/// <param name="Hints">what would make the mesh faster (a path through a hub that could be direct); nothing is wrong</param>
public sealed record MeshPlan(IReadOnlyDictionary<int, MeshApply> Configs, IReadOnlyList<MeshPath> Paths, IReadOnlyList<string> Problems,
    IReadOnlyDictionary<int, int?> Hubs, IReadOnlySet<int> PublicIds, IReadOnlyList<string>? Hints = null)
{
    public IReadOnlyList<string> Hints { get; } = Hints ?? [];
    /// <summary>Null when the two hosts share no network (they are not meant to talk).</summary>
    public MeshPath? Path(int from, int to) => Paths.FirstOrDefault(p => p.From == from && p.To == to);
    public bool IsHub(int agentId) => Hubs.Values.Contains(agentId);
}

/// <summary>
/// Turns the hosts of the mesh into one WireGuard config per host. Static topology, no probing. Hosts are grouped in
/// isolated networks and only hosts sharing one become peers. Within a network: a public host peers directly with
/// everyone; a host behind NAT keeps a keepalive to every public host; two hosts behind the same NAT talk over the LAN;
/// two hosts behind different NATs go through the network's hub (a public member that forwards, and only between that
/// network's members). Pure function: the same input gives the same configs and hashes.
/// </summary>
public static class MeshPlanner
{
    /// <summary>WireGuard's own default: room for its overhead over IPv6 on a 1500 link, which every path carries.</summary>
    public const int DefaultMtu = 1420;
    /// <summary>What WireGuard over IPv4 adds to a packet: 20 IP + 8 UDP + 32 of its own.</summary>
    public const int Overhead = 60;
    /// <summary>A tunnel MTU is kept within these: below, IPv4 tunnels make no sense; above, nothing fits a 1500 link.</summary>
    public const int MinMtu = 1280;
    public const int MaxMtu = 1500 - Overhead;
    public const int Keepalive = 25;

    /// <summary>
    /// The tunnel MTU of a host: the one set by hand for all, else from the smallest packet its measured paths carry
    /// (a path of 1500 gives 1440; 1492 behind PPPoE gives 1432: no fragmented packets, nothing wasted), else the default.
    /// </summary>
    public static int Mtu(MeshNode n, SettingsService.MeshSettings s) =>
        s.Mtu > 0 ? Math.Clamp(s.Mtu, MinMtu, MaxMtu) : n.PathMtu > 0 ? Math.Clamp(n.PathMtu - Overhead, MinMtu, MaxMtu) : DefaultMtu;

    // A host with a public address on its own interface (a VPS) is public by itself. The address an agent connection
    // comes from says little: docker-proxy and reverse proxies replace it, and a TCP port forwarded for the agents
    // says nothing about a UDP port. A host behind NAT is public only when marked so (after forwarding the mesh port).
    private static bool IsPublic(MeshNode n) => n.PublicOverride ?? n.HostIps.Any(IsPublicIp);

    public static bool IsPublicIp(string ip) =>
        IPAddress.TryParse(ip, out var a) && a.AddressFamily == AddressFamily.InterNetwork && !IsPrivate(ip)
        && a.GetAddressBytes() is not [0, ..] and not [>= 224, ..];

    /// <summary>"host:port" other hosts connect to, or null when nothing is known.</summary>
    public static string? Endpoint(MeshNode n, SettingsService.MeshSettings s)
    {
        if (!string.IsNullOrWhiteSpace(n.EndpointOverride))
        {
            var e = n.EndpointOverride.Trim();
            return HasPort(e) ? e : $"{e}:{n.ListenPort}";
        }
        var host = n.HostIps.FirstOrDefault(IsPublicIp)
            ?? (n.ServerHost && !string.IsNullOrEmpty(s.ServerPublicHost) ? s.ServerPublicHost : null)
            ?? (n.PeerIp != null && IsPublicIp(n.PeerIp) ? n.PeerIp : null);
        return host == null ? null : $"{host}:{n.ListenPort}";
    }

    /// <summary>The IPv4 address of "1.2.3.4:51821" as WireGuard reports an endpoint; null for anything else.</summary>
    public static string? EndpointHost(string? endpoint)
    {
        if (string.IsNullOrEmpty(endpoint) || endpoint.StartsWith('[')) return null;
        var colon = endpoint.LastIndexOf(':');
        var host = colon > 0 ? endpoint[..colon] : endpoint;
        return IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork ? host : null;
    }

    private static bool HasPort(string endpoint)
    {
        var colon = endpoint.LastIndexOf(':');
        return colon > 0 && endpoint.IndexOf(':') == colon && int.TryParse(endpoint[(colon + 1)..], out _);
    }

    /// <summary>A host or host:port the user typed: a name, an address and a port, no spaces, slashes or line breaks.</summary>
    public static bool IsEndpointText(string endpoint) =>
        endpoint.Length is > 0 and <= 255 && endpoint.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '[' or ']');

    /// <summary>A WireGuard key: 32 bytes in base64.</summary>
    public static bool IsWgKey(string? key)
    {
        Span<byte> bytes = stackalloc byte[33];
        return key is { Length: 44 } && Convert.TryFromBase64String(key, bytes, out var n) && n == 32;
    }

    /// <param name="balance">agent id → the balancers it runs for services of projects spread over servers (<see cref="MeshBalancers"/>)</param>
    public static MeshPlan Plan(IReadOnlyList<MeshNode> nodes, IReadOnlyList<MeshNetworkDef> networks, SettingsService.MeshSettings s,
        IReadOnlyDictionary<int, IReadOnlyList<MeshFrontend>>? balance = null)
    {
        var problems = new List<string>();
        var bits = SubnetAllocator.PrefixLength(s.Subnet);
        var byId = nodes.ToDictionary(n => n.AgentId);
        var names = nodes.ToDictionary(n => n.AgentId, n => n.Name);
        var publicIds = nodes.Where(IsPublic).Select(n => n.AgentId).ToHashSet();
        var endpoints = nodes.ToDictionary(n => n.AgentId, n => Endpoint(n, s));
        bool Reachable(int id) => publicIds.Contains(id) && endpoints[id] != null;
        foreach (var n in nodes.Where(n => publicIds.Contains(n.AgentId) && endpoints[n.AgentId] == null))
            problems.Add($"{n.Name} counts as public but has no address other servers could connect to — set its endpoint");

        // members that are in the mesh at all (a membership may outlive a leave until it is cleaned up)
        var nets = networks.Select(n => n with { Members = n.Members.Where(byId.ContainsKey).ToHashSet() }).OrderBy(n => n.Id).ToList();
        // each network's hub: the user's choice, else the Dockmaster server's host, else the first public member
        var hubs = nets.ToDictionary(n => n.Id, n =>
            n.HubOverride is { } chosen && n.Members.Contains(chosen) && Reachable(chosen) ? chosen
            : n.Members.Where(Reachable).OrderByDescending(id => byId[id].ServerHost).ThenBy(id => id).Cast<int?>().FirstOrDefault());

        var paths = new List<MeshPath>();
        var lan = new Dictionary<(int, int), string>();
        foreach (var a in nodes)
            foreach (var b in nodes.Where(b => b.AgentId != a.AgentId))
            {
                var shared = nets.Where(n => n.Members.Contains(a.AgentId) && n.Members.Contains(b.AgentId)).ToList();
                if (shared.Count == 0) continue; // different networks: isolated on purpose
                // a hub of a network both are in, that is neither of them
                var viaNet = shared.FirstOrDefault(n => hubs[n.Id] is { } h && h != a.AgentId && h != b.AgentId);
                PathKind kind;
                if (Reachable(a.AgentId) || Reachable(b.AgentId)) kind = PathKind.Direct;
                // the LAN only when it is sure (same public exit address); a guess from the /24 alone is only worth it without a hub
                else if (LanAddress(a, b, sure: viaNet != null) is { } lanIp) { kind = PathKind.Lan; lan[(a.AgentId, b.AgentId)] = lanIp; }
                else if (viaNet != null) kind = PathKind.ViaHub;
                else kind = PathKind.None;
                var net = kind == PathKind.ViaHub ? viaNet! : shared[0];
                // a trusted network on one LAN: no tunnel. For hosts behind one NAT that LAN is proven as above; public
                // hosts (VPS with a provider's private network) have only the shared private subnet — the user vouches.
                if (kind is PathKind.Lan or PathKind.Direct && a.PlainRoutes && b.PlainRoutes
                    && shared.FirstOrDefault(n => n.Trusted) is { } trusted
                    && (kind == PathKind.Lan ? lan[(a.AgentId, b.AgentId)] : LanAddress(a, b, sure: false)) is { } plainIp)
                {
                    kind = PathKind.Plain;
                    net = trusted;
                    lan[(a.AgentId, b.AgentId)] = plainIp;
                }
                paths.Add(new MeshPath(a.AgentId, b.AgentId, kind, kind == PathKind.ViaHub ? hubs[viaNet!.Id] : null, net.Id));
            }
        // relaying happens inside the tunnel: a host that goes through a hub keeps its tunnel to that hub, also when
        // the two are on one trusted LAN
        foreach (var via in paths.Where(p => p.Kind == PathKind.ViaHub).ToList())
            for (var i = 0; i < paths.Count; i++)
                if (paths[i].Kind == PathKind.Plain && (paths[i].From, paths[i].To) is var (f, t)
                    && ((f == via.From && t == via.HubId) || (t == via.From && f == via.HubId)))
                    paths[i] = paths[i] with { Kind = PathKind.Lan };
        foreach (var group in paths.Where(p => p.Kind == PathKind.None && p.From < p.To).GroupBy(p => p.NetworkId))
        {
            var net = nets.First(n => n.Id == group.Key);
            problems.Add($"{net.Name}: no way between " + string.Join(", ", group.Select(p => $"{names[p.From]} and {names[p.To]}"))
                + (hubs[net.Id] == null ? ": no member is reachable from the internet. Add a public server (a VPS) to this network, or forward a UDP port on a router to one member's mesh port and mark it public (endpoint host:port with the outside port)" : ""));
        }
        // a path through a hub works, but costs a second tunnel and the hub's bandwidth: one member made reachable
        // (a UDP port forwarded on its router) turns them all into direct paths
        var hints = new List<string>();
        foreach (var group in paths.Where(p => p.Kind == PathKind.ViaHub && p.From < p.To).GroupBy(p => p.NetworkId))
        {
            var net = nets.First(n => n.Id == group.Key);
            var behindNat = group.SelectMany(p => new[] { p.From, p.To }).Distinct().Where(id => !Reachable(id)).Order().Select(id => byId[id]).ToList();
            var hub = hubs[net.Id] is { } h ? names[h] : "a hub";
            hints.Add($"{net.Name}: {string.Join(", ", behindNat.Select(n => n.Name))} reach each other through {hub} — forward UDP port {behindNat[0].ListenPort} on the router of one of them to that server and mark it public in its panel (endpoint host:outside-port when the port differs); the others then connect to it directly, without the hop over the hub");
        }

        var configs = new Dictionary<int, MeshApply>();
        foreach (var a in nodes)
        {
            var aPublic = publicIds.Contains(a.AgentId);
            var cfg = new MeshApply { Address = $"{a.MeshIp}/{bits}", ListenPort = a.ListenPort, Subnet = s.Subnet, Hub = aPublic, Mtu = Mtu(a, s), TcpTuning = s.TcpTuning };
            // a public host may be asked to forward: only between members of the same network
            if (aPublic)
                foreach (var n in nets.Where(n => n.Members.Contains(a.AgentId) && n.Members.Count > 2))
                    cfg.Forward.Add(new MeshForward { Network = n.Name, Ips = { n.Members.Where(id => id != a.AgentId).Select(id => byId[id].MeshIp).Order(StringComparer.Ordinal) } });
            var peers = new Dictionary<int, MeshPeer>();
            MeshPeer PeerFor(MeshNode b)
            {
                if (!peers.TryGetValue(b.AgentId, out var p))
                    peers[b.AgentId] = p = new MeshPeer { Name = b.Name, PublicKey = b.PublicKey, MeshIp = b.MeshIp, AllowedIps = { b.MeshIp + "/32" } };
                return p;
            }
            foreach (var b in nodes.Where(b => b.AgentId != a.AgentId))
            {
                var path = paths.FirstOrDefault(p => p.From == a.AgentId && p.To == b.AgentId);
                if (path == null) continue;
                switch (path.Kind)
                {
                    case PathKind.Direct:
                    {
                        var p = PeerFor(b);
                        var bPublic = Reachable(b.AgentId);
                        if (bPublic) p.Endpoint = endpoints[b.AgentId]!;
                        // the side behind NAT keeps the hole open; two public hosts need no keepalive
                        if (bPublic && !aPublic) p.Keepalive = Keepalive;
                        break;
                    }
                    case PathKind.Lan:
                    {
                        var p = PeerFor(b);
                        p.Endpoint = $"{lan[(a.AgentId, b.AgentId)]}:{b.ListenPort}";
                        p.Keepalive = Keepalive;
                        break;
                    }
                    case PathKind.Plain:
                        cfg.Routes.Add(new MeshRoute { MeshIp = b.MeshIp, Via = lan[(a.AgentId, b.AgentId)] });
                        break;
                    case PathKind.ViaHub:
                    {
                        var hubNode = nodes.First(n => n.AgentId == path.HubId);
                        var p = PeerFor(hubNode); // exists as a direct peer too; only the routed /32 is added here
                        if (!p.AllowedIps.Contains(b.MeshIp + "/32")) p.AllowedIps.Add(b.MeshIp + "/32");
                        break;
                    }
                }
            }
            cfg.Balance.AddRange((balance?.GetValueOrDefault(a.AgentId) ?? []).OrderBy(f => f.Name, StringComparer.Ordinal));
            var routes = cfg.Routes.OrderBy(r => r.MeshIp, StringComparer.Ordinal).ToList();
            cfg.Routes.Clear();
            cfg.Routes.AddRange(routes);
            foreach (var p in peers.Values.OrderBy(p => p.MeshIp, StringComparer.Ordinal))
            {
                var sorted = p.AllowedIps.OrderBy(x => x, StringComparer.Ordinal).ToList();
                p.AllowedIps.Clear();
                p.AllowedIps.AddRange(sorted);
                cfg.Peers.Add(p);
            }
            cfg.ConfigHash = Hash(cfg);
            configs[a.AgentId] = cfg;
        }
        return new MeshPlan(configs, paths, problems, hubs, publicIds, hints);
    }

    /// <summary>
    /// Two hosts on one LAN: an address of b in a /24 that a is in too, else null. With <paramref name="sure"/> both
    /// must also leave through the same public address (the same NAT); without it the shared /24 is enough — two
    /// homes on 192.168.1.x would then be taken for one LAN, which is why that guess is used only when there is no hub.
    /// </summary>
    private static string? LanAddress(MeshNode a, MeshNode b, bool sure = true)
    {
        var exit = PublicExit(a);
        if (sure && (exit == null || exit != PublicExit(b))) return null;
        foreach (var ip in b.HostIps.Where(IsPrivate))
            if (a.HostIps.Any(mine => IsPrivate(mine) && Same24(mine, ip))) return ip;
        return null;
    }

    /// <summary>The public address a host's traffic leaves through: what the mesh sees, else where its agent connects from.</summary>
    private static string? PublicExit(MeshNode n) =>
        n.SeenFrom is { } seen && IsPublicIp(seen) ? seen
        : n.PeerIp is { } peer && IsPublicIp(peer) ? peer
        : null;

    private static bool Same24(string x, string y)
    {
        var px = x.LastIndexOf('.');
        var py = y.LastIndexOf('.');
        return px > 0 && py > 0 && x[..px] == y[..py];
    }

    public static bool IsPrivate(string ip)
    {
        if (!IPAddress.TryParse(ip, out var a) || a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 100 && b[1] is >= 64 and <= 127 || b[0] == 127 || b[0] == 169 && b[1] == 254;
    }

    /// <summary>Stable digest of everything the agent writes, so the server can tell an applied config from a stale one.</summary>
    public static string Hash(MeshApply cfg)
    {
        var sb = new StringBuilder();
        sb.Append(cfg.Address).Append('|').Append(cfg.ListenPort).Append('|').Append(cfg.Subnet).Append('|').Append(cfg.Hub).Append('|').Append(cfg.Mtu);
        if (cfg.TcpTuning) sb.Append("|tune"); // only when on: the hash of a host without it stays what it was
        foreach (var p in cfg.Peers.OrderBy(p => p.PublicKey, StringComparer.Ordinal))
            sb.Append('\n').Append(p.PublicKey).Append('|').Append(p.MeshIp).Append('|').Append(p.Endpoint).Append('|')
              .Append(string.Join(",", p.AllowedIps.OrderBy(x => x, StringComparer.Ordinal))).Append('|').Append(p.Keepalive);
        foreach (var f in cfg.Forward.OrderBy(f => f.Network, StringComparer.Ordinal))
            sb.Append("\nfwd ").Append(f.Network).Append('|').Append(string.Join(",", f.Ips.OrderBy(x => x, StringComparer.Ordinal)));
        foreach (var f in cfg.Balance.OrderBy(f => f.Name, StringComparer.Ordinal))
            sb.Append("\nlb ").Append(f.Name).Append('|').Append(f.Bind).Append('|').Append(string.Join(",", f.Backends.OrderBy(x => x, StringComparer.Ordinal)));
        foreach (var r in cfg.Routes.OrderBy(r => r.MeshIp, StringComparer.Ordinal))
            sb.Append("\nroute ").Append(r.MeshIp).Append('|').Append(r.Via);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
    }
}
