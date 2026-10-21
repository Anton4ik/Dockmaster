using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Dockmaster.Protocol;

namespace Dockmaster.Agent.Docker;

/// <summary>Pure text work of the mesh: the wg config, and the line-based output of the dm-mesh sidecar script.</summary>
public static class MeshConfig
{
    /// <summary>UDP port of dm0 when the server does not ask for one.</summary>
    public const int DefaultPort = 51821;
    /// <summary>MTU of dm0 when the server does not set one: WireGuard's own default, room for its header over a 1500 link.</summary>
    public const int DefaultMtu = 1420;

    /// <summary>The `wg setconf` file for a MeshApply (only wg keys: Address / MTU are set by the script).</summary>
    public static string WgConf(MeshApply req, string privateKey)
    {
        var sb = new StringBuilder();
        sb.Append("[Interface]\n");
        sb.Append("PrivateKey = ").Append(privateKey.Trim()).Append('\n');
        sb.Append("ListenPort = ").Append(req.ListenPort).Append('\n');
        // every value stays on its own line: a name or key with a line break in it would add AllowedIPs of the sender's
        // choosing to the configs of all the other hosts
        foreach (var p in req.Peers.Where(p => IsWgKey(p.PublicKey)).OrderBy(p => p.MeshIp, StringComparer.Ordinal))
        {
            sb.Append("\n[Peer]\n");
            sb.Append("# ").Append(Clean(p.Name)).Append(' ').Append(IsIp(p.MeshIp) ? p.MeshIp : "").Append('\n');
            sb.Append("PublicKey = ").Append(p.PublicKey).Append('\n');
            sb.Append("AllowedIPs = ").Append(string.Join(", ", p.AllowedIps.Where(IsCidr))).Append('\n');
            if (IsEndpoint(p.Endpoint)) sb.Append("Endpoint = ").Append(p.Endpoint).Append('\n');
            if (p.Keepalive > 0) sb.Append("PersistentKeepalive = ").Append(p.Keepalive).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// The hub's forwarding groups, one line per network: "name&lt;TAB&gt;ip,ip,…". dm-mesh lets traffic through dm0→dm0
    /// only between addresses on one line, so a hub shared by two networks does not bridge them.
    /// </summary>
    public static string ForwardFile(MeshApply req) =>
        string.Concat(req.Forward.Where(f => f.Ips.Count > 1).Select(f => $"{Clean(f.Network)}\t{string.Join(",", f.Ips.Where(IsIp))}\n"));

    /// <summary>
    /// HAProxy's config for the balancers (TCP, least connections, a replica that fails 2 checks 2 s apart is left
    /// out until it answers again); "" when there are none. Anything that is not a plain name or address is skipped:
    /// the file is read by the sidecar.
    /// </summary>
    public static string HaproxyConfig(MeshApply req)
    {
        var frontends = req.Balance.Where(f => IsName(f.Name) && IsIpPort(f.Bind)).ToList();
        if (frontends.Count == 0) return "";
        var sb = new StringBuilder();
        sb.Append("# written by the Dockmaster agent: balancers for services whose replicas run on other servers\n");
        // it binds its ports as root, then drops to the haproxy user in an empty directory
        sb.Append("global\n    maxconn 20000\n    chroot /var/lib/haproxy\n    user haproxy\n    group haproxy\n\n");
        sb.Append("defaults\n    mode tcp\n    timeout connect 3s\n    timeout client 1h\n    timeout server 1h\n");
        sb.Append("    retries 2\n    option redispatch\n    default-server inter 2s fall 2 rise 2\n");
        foreach (var f in frontends.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            // a frontend and a backend may not share a name (HAProxy 3.3+)
            sb.Append("\nfrontend fe_").Append(f.Name).Append("\n    bind ").Append(f.Bind).Append("\n    default_backend be_").Append(f.Name).Append('\n');
            sb.Append("\nbackend be_").Append(f.Name).Append("\n    balance leastconn\n");
            var n = 0;
            foreach (var b in f.Backends.Where(IsIpPort).Distinct().Order(StringComparer.Ordinal))
                sb.Append("    server r").Append(++n).Append(' ').Append(b).Append(" check\n");
        }
        return sb.ToString();
    }

    private static bool IsName(string s) => s.Length is > 0 and <= 100 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
    private static bool IsIpPort(string s) =>
        s.LastIndexOf(':') is var i and > 0 && IsIp(s[..i]) && int.TryParse(s[(i + 1)..], out var port) && port is > 0 and < 65536;

    /// <summary>Peers reached outside the tunnel: one "meshIp lanIp" line each (only well-formed addresses).</summary>
    public static string RoutesFile(MeshApply req) =>
        string.Concat(req.Routes.Where(r => IsIp(r.MeshIp) && IsIp(r.Via)).Select(r => $"{r.MeshIp} {r.Via}\n"));

    private static string Clean(string s) => new(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
    private static bool IsIp(string s) => IPAddress.TryParse(s, out var a) && a.AddressFamily == AddressFamily.InterNetwork;
    private static bool IsCidr(string s) =>
        s.IndexOf('/') is var i and > 0 && IsIp(s[..i]) && int.TryParse(s[(i + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var bits) && bits <= 32;

    /// <summary>A WireGuard key: 32 bytes in base64.</summary>
    public static bool IsWgKey(string s)
    {
        Span<byte> bytes = stackalloc byte[33];
        return s.Length == 44 && Convert.TryFromBase64String(s, bytes, out var n) && n == 32;
    }

    /// <summary>host:port as WireGuard takes it (a name, an IPv4 address or [IPv6]), nothing else.</summary>
    public static bool IsEndpoint(string s) =>
        s.Length is > 0 and <= 260 && s.LastIndexOf(':') is var i and > 0
        && int.TryParse(s[(i + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536
        && s[..i].All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '[' or ']');

    /// <summary>Environment of the sidecar script (sourced by sh: values are plain, no quoting needed).</summary>
    public static string EnvFile(MeshApply req) =>
        $"ADDRESS={req.Address}\nSUBNET={req.Subnet}\nPORT={req.ListenPort}\nHUB={(req.Hub ? 1 : 0)}\nMTU={(req.Mtu > 0 ? req.Mtu : DefaultMtu)}\nHASH={req.ConfigHash}\nTUNE={(req.TcpTuning ? 1 : 0)}\n";

    /// <summary>The largest IP packet the path to a peer's endpoint carries; 0 = could not be measured.</summary>
    public sealed record PathMtu(string PublicKey, string Host, int Mtu);

    /// <summary>`dm-mesh pmtu` output: "pmtu key host size" lines, anything else skipped.</summary>
    public static List<PathMtu> ParsePmtu(string text)
    {
        var list = new List<PathMtu>();
        foreach (var raw in text.Split('\n'))
        {
            var f = raw.TrimEnd('\r').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f is ["pmtu", var key, var host, var size] && int.TryParse(size, out var mtu)) list.Add(new PathMtu(key, host, Math.Max(0, mtu)));
        }
        return list;
    }

    public sealed record Probe(bool Kernel, bool Tun, bool PortBusy, List<string> HostIps, List<string> Routes, List<string> Addrs);

    /// <summary>`dm-mesh probe` output.</summary>
    public static Probe ParseProbe(string text)
    {
        var p = new Probe(false, false, false, [], [], []);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var sp = line.IndexOf(' ');
            if (sp < 0) continue;
            var key = line[..sp];
            var val = line[(sp + 1)..];
            switch (key)
            {
                case "kernel": p = p with { Kernel = val == "true" }; break;
                case "tun": p = p with { Tun = val == "true" }; break;
                case "port": p = p with { PortBusy = val.EndsWith(" busy") }; break;
                case "hostip": p.HostIps.Add(val.Trim()); break;
                case "route": p.Routes.Add(val.Trim()); break;
                case "addr": p.Addrs.Add(val.Trim()); break;
            }
        }
        return p;
    }

    /// <summary>Routes and addresses of the host that intersect the mesh subnet ("10.77.0.0/24 dev eth1"), for the join to refuse.</summary>
    public static List<string> Overlaps(string subnet, Probe probe)
    {
        var result = new List<string>();
        if (!TryParseCidr(subnet, out var net, out var bits)) return result;
        foreach (var r in probe.Routes)
        {
            var first = r.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (first == null || first == "default" || !first.Contains('/')) continue;
            if (TryParseCidr(first, out var n2, out var b2) && Intersects(net, bits, n2, b2)) result.Add(r);
        }
        foreach (var a in probe.Addrs)
        {
            // "192.168.1.5/24 eth0" -> the interface's network
            var parts = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1 || !TryParseCidr(parts[0], out var n2, out var b2)) continue;
            if (Intersects(net, bits, n2, b2)) result.Add($"{parts[0]} on {(parts.Length > 1 ? parts[1] : "?")}");
        }
        return result;
    }

    public static bool TryParseCidr(string cidr, out uint network, out int bits)
    {
        network = 0;
        bits = 32;
        var slash = cidr.IndexOf('/');
        var ipText = slash < 0 ? cidr : cidr[..slash];
        if (!IPAddress.TryParse(ipText, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        if (slash >= 0 && (!int.TryParse(cidr[(slash + 1)..], out bits) || bits is < 0 or > 32)) return false;
        network = ToUint(ip) & Mask(bits);
        return true;
    }

    private static uint ToUint(IPAddress ip) { var b = ip.GetAddressBytes(); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
    private static uint Mask(int bits) => bits == 0 ? 0 : uint.MaxValue << (32 - bits);
    private static bool Intersects(uint n1, int b1, uint n2, int b2) { var m = Mask(Math.Min(b1, b2)); return (n1 & m) == (n2 & m); }

    /// <summary>
    /// `dm-mesh status` output into a MeshStatus. <paramref name="meshIpOf"/> maps a peer's public key to its mesh ip
    /// (from the config that was applied: the dump only shows allowed ips, which include routed /32s on a hub).
    /// </summary>
    public static MeshStatus ParseStatus(string text, Func<string, string?> meshIpOf)
    {
        var s = new MeshStatus();
        var rtts = new Dictionary<string, double>();
        var first = true;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("wg\t"))
            {
                var f = line[3..].Split('\t');
                if (first)
                {
                    // interface line: private-key public-key listen-port fwmark
                    first = false;
                    if (f.Length >= 3) { s.PublicKey = f[1]; s.ListenPort = int.TryParse(f[2], out var lp) ? lp : 0; }
                    continue;
                }
                // peer line: public-key preshared-key endpoint allowed-ips latest-handshake rx tx keepalive
                if (f.Length < 7) continue;
                s.Peers.Add(new MeshPeerStatus
                {
                    PublicKey = f[0],
                    MeshIp = meshIpOf(f[0]) ?? f[3].Split(',')[0].Split('/')[0],
                    Endpoint = f[2] == "(none)" ? "" : f[2],
                    LastHandshakeUnix = long.TryParse(f[4], out var hs) ? hs : 0,
                    Rx = long.TryParse(f[5], out var rx) ? rx : 0,
                    Tx = long.TryParse(f[6], out var tx) ? tx : 0,
                    RttMs = -1,
                });
                continue;
            }
            var sp = line.IndexOf(' ');
            if (sp < 0) continue;
            var key = line[..sp];
            var val = line[(sp + 1)..].Trim();
            switch (key)
            {
                case "backend": s.Backend = val == "none" ? "" : val; break;
                case "up": s.Up = val == "true"; break;
                case "kernel": s.KernelAvailable = val == "true"; break;
                case "tcp_cc": s.TcpCongestion = val == "unknown" ? "" : val; break;
                case "hostip": s.HostIps.Add(val); break;
                case "problem": s.Problems.Add(val); break;
                case "rtt":
                {
                    var parts = val.Split(' ');
                    if (parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)) rtts[parts[0]] = ms;
                    break;
                }
            }
        }
        foreach (var p in s.Peers)
            if (rtts.TryGetValue(p.MeshIp, out var ms)) p.RttMs = ms;
        foreach (var (ip, ms) in rtts) s.RttMs[ip] = ms;
        return s;
    }

    /// <summary>public key → mesh ip, from the "# name ip" comment WgConf writes above each peer.</summary>
    public static Dictionary<string, string> PeerMap(string conf)
    {
        var map = new Dictionary<string, string>();
        string? ip = null;
        foreach (var raw in conf.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("# ")) ip = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            else if (line.StartsWith("PublicKey") && ip != null) { map[line[(line.IndexOf('=') + 1)..].Trim()] = ip; ip = null; }
        }
        return map;
    }

    /// <summary>A value of the env file <see cref="EnvFile"/> wrote, "" when it is not there.</summary>
    public static string EnvValue(string env, string key) =>
        env.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith(key + "="))?[(key.Length + 1)..] ?? "";
}
