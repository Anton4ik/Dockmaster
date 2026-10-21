using System.Globalization;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Dockmaster.Server.Services;

/// <summary>
/// One entry of a service's `ports:` ("127.0.0.1:8765:8080/tcp", "8765-8766:8080", {published, target, …}).
/// </summary>
/// <param name="Published">the host port or range ("8765", "8765-8766"); "" when docker picks one</param>
public sealed record PortEntry(string? HostIp, string Published, string Target, string Proto)
{
    /// <summary>A host port that one container at a time can hold (a range holds one container per port).</summary>
    public bool Fixed => Published.Length > 0;
    public bool Tcp => Proto is "" or "tcp";

    /// <summary>How many replicas the host side lets run: the size of a range, 1 for one port; null when docker picks.</summary>
    public int? Room => !Fixed ? null
        : Range(Published) is var (a, b) && !Target.Contains('-') ? b - a + 1
        : 1;

    /// <summary>The first host port, when it is a plain number (not a ${VARIABLE}).</summary>
    public int? FirstPort => Range(Published)?.From;

    /// <summary>Short syntax, as the file would have it.</summary>
    public string Short()
    {
        var ip = HostIp is null or "" ? "" : (HostIp.Contains(':') ? $"[{HostIp}]" : HostIp) + ":";
        return (Published.Length > 0 || ip.Length > 0 ? ip + Published + ":" : "") + Target + (Tcp ? "" : "/" + Proto);
    }

    public static PortEntry? Parse(YamlNode node) => node switch
    {
        YamlMappingNode m when m.Scalar("target") is { Length: > 0 } target =>
            new PortEntry(m.Scalar("host_ip"), m.Scalar("published") ?? "", target, (m.Scalar("protocol") ?? "").ToLowerInvariant()),
        YamlScalarNode { Value: { Length: > 0 } v } => Parse(v),
        _ => null,
    };

    public static PortEntry Parse(string value)
    {
        var slash = value.IndexOf('/');
        var proto = slash < 0 ? "" : value[(slash + 1)..].ToLowerInvariant();
        var main = slash < 0 ? value : value[..slash];
        var last = main.LastIndexOf(':');
        if (last < 0) return new PortEntry(null, "", main, proto);
        var rest = main[..last];
        var colon = rest.LastIndexOf(':');
        // "[::1]:8080:80": the address may hold colons itself, in brackets
        if (rest.EndsWith(']') || (colon >= 0 && rest.LastIndexOf(']') > colon)) colon = -1;
        return colon < 0 && rest.StartsWith('[')
            ? new PortEntry(rest.Trim('[', ']'), "", main[(last + 1)..], proto)
            : colon < 0
                ? new PortEntry(null, rest, main[(last + 1)..], proto)
                : new PortEntry(rest[..colon].Trim('[', ']'), rest[(colon + 1)..], main[(last + 1)..], proto);
    }

    /// <summary>"8765" → (8765, 8765), "8765-8766" → (8765, 8766); null for anything else.</summary>
    public static (int From, int To)? Range(string published)
    {
        var parts = published.Split('-');
        if (parts.Length is < 1 or > 2 || !parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit))) return null;
        var from = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var to = int.Parse(parts[^1], CultureInfo.InvariantCulture);
        return from is > 0 and < 65536 && to >= from && to < 65536 ? (from, to) : null;
    }
}

/// <summary>
/// Several replicas of a service that publishes a fixed host port ("8765:8080"): one container at a time can hold
/// the port. The person picks one of two ways (the UI asks):
/// - balance (x-dockmaster.balance: true): at deploy the service stops publishing its fixed TCP ports and a
///   `{service}-balancer` (HAProxy) takes them over and spreads the connections over the replicas. It finds them by
///   the service's name in docker's DNS, so replicas that come and go (a rolling update, the autoscaler) are picked
///   up without touching the balancer;
/// - range ("8765-8766:8080" in the file): each replica takes a port of the range, see <see cref="WidenRanges"/>.
/// </summary>
public static class PortBalancer
{
    public const string Suffix = "-balancer";
    public const string Image = "haproxy:3.0-alpine";
    public const string Label = "dockmaster.balancer";
    /// <summary>Replicas one balancer can reach: fixed, so a new replica count does not change (and re-create) it.</summary>
    private const int Slots = 100;

    public static string NameOf(string service) => service + Suffix;
    public static bool Enabled(YamlMappingNode? service) => service.Flag("x-dockmaster", "balance") == true;

    /// <summary>
    /// The service runs a balancer: it asks for one and something needs it — several replicas, the autoscaler may
    /// start more, or a rolling update of one replica (old and new side by side). Back at 1 replica the balancer goes
    /// and the service holds its ports again; x-dockmaster.balance stays, the choice for when it has more.
    /// </summary>
    public static bool Needed(YamlMappingNode? service) =>
        Enabled(service) && Balanced(service).Count > 0
        && ((int.TryParse(service.Scalar("deploy", "replicas"), out var r) ? r : 1) > 1
            || ComposeServices.Autoscale(service) is { Auto: true, Max: > 1 }
            || service.Flag("x-dockmaster", "rolling") == true);

    /// <summary>The ports a balancer takes over: fixed single TCP ports.</summary>
    public static List<PortEntry> Balanced(YamlMappingNode? service) =>
        (service.Child("ports") as YamlSequenceNode)?.Children.Select(PortEntry.Parse).OfType<PortEntry>()
            .Where(p => p.Fixed && p.Tcp && p.Room == 1).ToList() ?? [];

    /// <summary>What stops the balancer of a service (null when it can run).</summary>
    public static string? Problem(string name, YamlMappingNode service, YamlMappingNode services)
    {
        if (services.Child(NameOf(name)) != null) return $"service {name}: a service named {NameOf(name)} exists already, the balancer needs that name — rename it or turn x-dockmaster.balance off";
        if (service.Scalar("network_mode") is { } mode) return $"service {name}: a balancer cannot reach replicas with network_mode: {mode} — remove x-dockmaster.balance";
        var ports = (service.Child("ports") as YamlSequenceNode)?.Children.Select(PortEntry.Parse).OfType<PortEntry>().ToList() ?? [];
        if (ports.FirstOrDefault(p => p.Fixed && !p.Tcp) is { } udp) return $"service {name}: UDP port {udp.Short()} cannot be balanced — publish it as a range, or run 1 replica";
        if (ports.FirstOrDefault(p => p.Fixed && p.Tcp && p.FirstPort == null) is { } variable) return $"service {name}: port {variable.Short()} is not a plain number, the balancer cannot take it over";
        return null;
    }

    /// <summary>
    /// The services of a compose file (as the deploy sends it) with a balancer where x-dockmaster.balance asks for
    /// one: the service keeps its other ports, the balancer publishes the fixed TCP ones. Problems are left to
    /// <see cref="Problem"/>: a service with one is not touched.
    /// </summary>
    public static void Apply(YamlMappingNode root)
    {
        if (root.Child("services") is not YamlMappingNode services) return;
        foreach (var (key, node) in services.Children.ToList())
        {
            var name = key.Text();
            if (node is not YamlMappingNode svc || !Needed(svc) || Problem(name, svc, services) != null) continue;
            var taken = Balanced(svc);
            var seq = (YamlSequenceNode)svc.Child("ports")!;
            foreach (var item in seq.Children.ToList())
                if (PortEntry.Parse(item) is { } p && taken.Contains(p)) seq.Children.Remove(item);
            if (seq.Children.Count == 0) svc.Children.Remove(new YamlScalarNode("ports"));
            services.Children[new YamlScalarNode(NameOf(name))] = Service(name, svc, taken);
        }
    }

    /// <summary>
    /// The wanted services with the balancers of those that have one in <paramref name="yaml"/> (the file as it is sent,
    /// after <see cref="Apply"/>): a deploy or restart of one takes its balancer along.
    /// </summary>
    public static List<string>? WithBalancers(List<string>? wanted, string yaml)
    {
        if (wanted == null || !yaml.Contains(Label)) return wanted;
        try
        {
            var services = YamlNodes.LoadRoot(yaml).Child("services") as YamlMappingNode;
            return wanted.Concat(wanted.Where(s => services.Child(NameOf(s)) is YamlMappingNode lb && lb.Scalar("labels", Label) == s).Select(NameOf))
                .Distinct().ToList();
        }
        catch (YamlException) { return wanted; }
    }

    private static YamlMappingNode Service(string name, YamlMappingNode svc, List<PortEntry> ports)
    {
        var lb = new YamlMappingNode
        {
            { "image", Image },
            // it binds the ports as root (also below 1024), HAProxy's workers run as the haproxy user
            { "user", "root" },
            { "entrypoint", new YamlSequenceNode(Quoted("/bin/sh"), Quoted("-c"), Quoted("printf '%s\\n' \"$$DM_HAPROXY_CFG\" > /tmp/haproxy.cfg && exec haproxy -W -db -f /tmp/haproxy.cfg")) },
            { "environment", new YamlMappingNode { { "DM_HAPROXY_CFG", new YamlScalarNode(Config(name, ports)) { Style = ScalarStyle.Literal } } } },
            { "ports", new YamlSequenceNode(ports.Select(p => Quoted(p with { Target = p.Published, Proto = "" }))) },
            { "restart", svc.Scalar("restart") ?? "unless-stopped" },
            // deployed after the service: the old container of a service that held the port itself lets it go first
            { "depends_on", new YamlSequenceNode(Quoted(name)) },
            { "labels", new YamlMappingNode { { Label, Quoted(name) } } },
        };
        // the same networks as the service, so its name resolves to the replicas
        var networks = svc.Child("networks") switch
        {
            YamlSequenceNode s => s.Scalars().ToList(),
            YamlMappingNode m => m.Children.Keys.Select(k => k.Text()).ToList(),
            _ => [],
        };
        if (networks.Count > 0) lb.Add("networks", new YamlSequenceNode(networks.Select(n => new YamlScalarNode(n))));
        return lb;
    }

    private static YamlScalarNode Quoted(string value) => new(value) { Style = ScalarStyle.DoubleQuoted };
    private static YamlScalarNode Quoted(PortEntry p) => Quoted(p.Short());

    /// <summary>
    /// HAProxy's config: one frontend per port; the backend holds <see cref="Slots"/> places filled from docker's
    /// DNS (the service's name gives every running replica), least connections, a replica that fails 2 checks 2 s
    /// apart is left out until it answers again.
    /// </summary>
    public static string Config(string service, IReadOnlyList<PortEntry> ports)
    {
        var sb = new StringBuilder();
        sb.Append("# written by Dockmaster: spreads the connections to ").Append(service).Append(" over its replicas\n");
        // a maxconn within the 4096 files a container may have open on some hosts (Synology)
        sb.Append("global\n  maxconn 2000\n  user haproxy\n  group haproxy\n");
        sb.Append("resolvers docker\n  nameserver dns 127.0.0.11:53\n  accepted_payload_size 8192\n  hold valid 2s\n  hold obsolete 5s\n");
        sb.Append("defaults\n  mode tcp\n  timeout connect 3s\n  timeout client 1h\n  timeout server 1h\n  retries 2\n  option redispatch\n");
        sb.Append("  default-server inter 2s fall 2 rise 2 resolvers docker init-addr none\n");
        foreach (var p in ports)
        {
            sb.Append("frontend fe_").Append(p.Published).Append("\n  bind :").Append(p.Published).Append("\n  default_backend be_").Append(p.Published).Append('\n');
            sb.Append("backend be_").Append(p.Published).Append("\n  balance leastconn\n  server-template r 1-").Append(Slots).Append(' ').Append(service).Append(':').Append(p.Target).Append(" check\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// The ports for a balancer: a range a port each ("8765-8766:8080", written for a range before) is its first
    /// port again, which the balancer holds. Null when nothing changes (or entries with options, left as they are).
    /// </summary>
    public static List<string>? Narrow(YamlMappingNode service)
    {
        if (service.Child("ports") is not YamlSequenceNode seq || seq.Children.Any(c => c is not YamlScalarNode)) return null;
        var entries = seq.Children.Select(PortEntry.Parse).OfType<PortEntry>().ToList();
        if (!entries.Any(p => p.Room > 1 && p.Tcp)) return null;
        return entries.Select(p => p.Room > 1 && p.Tcp ? (p with { Published = p.FirstPort!.Value.ToString(CultureInfo.InvariantCulture) }).Short() : p.Short()).ToList();
    }

    /// <summary>
    /// The fixed ports of a service as ranges wide enough for <paramref name="replicas"/>: "8765:8080" →
    /// "8765-8766:8080" for 2; a range already wide enough stays. Null when the ports cannot be written that way.
    /// </summary>
    public static List<string>? WidenRanges(YamlMappingNode service, int replicas, out string? problem)
    {
        problem = null;
        var list = new List<string>();
        foreach (var node in (service.Child("ports") as YamlSequenceNode)?.Children ?? [])
        {
            if (node is not YamlScalarNode || PortEntry.Parse(node) is not { } p)
            {
                problem = "the ports are written with options; change them in the compose file";
                return null;
            }
            if (!p.Fixed || p.Room >= replicas) { list.Add(p.Short()); continue; }
            if (p.FirstPort is not { } from || p.Target.Contains('-'))
            {
                problem = $"port {p.Short()} cannot be made a range; change it in the compose file";
                return null;
            }
            if (from + replicas - 1 > 65535)
            {
                problem = $"port {p.Short()}: a range of {replicas} ports from {from} goes past 65535";
                return null;
            }
            list.Add((p with { Published = $"{from}-{from + replicas - 1}" }).Short());
        }
        return list;
    }
}
