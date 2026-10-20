using Dockmaster.Server.Services;
using YamlDotNet.RepresentationModel;
using L = Dockmaster.Server.Services.ComposeEditor.ServiceLimits;

namespace Dockmaster.Tests;

public class PortBalancerTests
{
    private const string Web = """
        services:
          web:
            image: app:1
            container_name: web
            ports:
              - "8765:8080"
              - "9000"
            restart: always
            deploy:
              replicas: 1
        """;

    private static ServiceSummary Svc(string yaml, string name) => ComposeServices.Parse(yaml).Single(s => s.Name == name);
    private static YamlMappingNode Root(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }
    private static YamlNode? At(YamlNode node, params string[] path) =>
        path.Aggregate((YamlNode?)node, (n, key) => n is YamlMappingNode m && m.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v : null);
    private static string? Text(YamlNode node, params string[] path) => (At(node, path) as YamlScalarNode)?.Value;
    private static string[] List(YamlNode node, params string[] path) => (At(node, path) as YamlSequenceNode)?.Children.Select(c => ((YamlScalarNode)c).Value!).ToArray() ?? [];
    private static YamlMappingNode Service(string yaml, string name) => (YamlMappingNode)At(Root(yaml), "services", name)!;
    private static string Save(YamlMappingNode root)
    {
        var w = new StringWriter();
        new YamlStream(new YamlDocument(root)).Save(w, assignAnchors: false);
        return w.ToString();
    }

    [Theory]
    [InlineData("8765:8080", null, "8765", "8080", "", 1)]
    [InlineData("127.0.0.1:8765:8080/tcp", "127.0.0.1", "8765", "8080", "tcp", 1)]
    [InlineData("[::1]:8765:8080", "::1", "8765", "8080", "", 1)]
    [InlineData("8765-8767:8080", null, "8765-8767", "8080", "", 3)]
    [InlineData("127.0.0.1::8080", "127.0.0.1", "", "8080", "", null)]
    [InlineData("8080", null, "", "8080", "", null)]
    [InlineData("53:53/udp", null, "53", "53", "udp", 1)]
    public void Parses_short_port_entries(string value, string? ip, string published, string target, string proto, int? room)
    {
        var p = PortEntry.Parse(value);
        Assert.Equal((ip, published, target, proto, room), (p.HostIp, p.Published, p.Target, p.Proto, p.Room));
    }

    [Fact]
    public void Summary_tells_how_many_replicas_the_ports_allow()
    {
        var s = Svc(Web, "web");
        Assert.Equal((1, false, false), (s.PortRoom, s.Balance, s.Rolling));
        var balanced = Svc(Web.Replace("restart: always", "restart: always\n    x-dockmaster:\n      balance: true").Replace("container_name: web\n    ", ""), "web");
        Assert.Equal((null, true), (balanced.PortRoom, balanced.Balance));
        Assert.Equal(3, Svc(Web.Replace("8765:8080", "8765-8767:8080"), "web").PortRoom);
    }

    [Fact]
    public void More_replicas_with_a_balancer_keep_the_port_and_drop_the_name()
    {
        var yaml = ComposeEditor.SetLimits(Web, "web", new L(3, null, null, ComposeEditor.PortsBalance));
        var s = Svc(yaml, "web");
        Assert.Equal((3, true, null, (string?)null), (s.Replicas, s.Balance, s.PortRoom, s.ContainerName));
        Assert.Contains("\"8765:8080\"", yaml);
        Assert.True(s.Rolling);
    }

    [Fact]
    public void More_replicas_with_a_range_widen_the_fixed_ports()
    {
        var yaml = ComposeEditor.SetLimits(Web, "web", new L(2, null, null, ComposeEditor.PortsRange));
        var s = Svc(yaml, "web");
        Assert.Equal(["8765-8766:8080", "9000"], s.PortList!);
        Assert.Equal((2, 2, false), (s.Replicas, s.PortRoom, s.Balance));
        // wide enough already: the file stays as written
        Assert.Equal(yaml, ComposeEditor.SetLimits(yaml, "web", new L(2, null, null, ComposeEditor.PortsRange)));
    }

    [Fact]
    public void A_range_replaces_the_balancer()
    {
        var balanced = ComposeEditor.SetLimits(Web, "web", new L(2, null, null, ComposeEditor.PortsBalance));
        var s = Svc(ComposeEditor.SetLimits(balanced, "web", new L(3, null, null, ComposeEditor.PortsRange)), "web");
        Assert.Equal((false, 3), (s.Balance, s.PortRoom));
    }

    [Fact]
    public void A_balancer_replaces_the_range()
    {
        var ranged = ComposeEditor.SetLimits(Web, "web", new L(2, null, null, ComposeEditor.PortsRange));
        var s = Svc(ComposeEditor.SetLimits(ranged, "web", new L(3, null, null, ComposeEditor.PortsBalance)), "web");
        Assert.Equal(["8765:8080", "9000"], s.PortList!);
        Assert.Equal((true, (int?)null), (s.Balance, s.PortRoom));
    }

    [Fact]
    public void Autoscale_auto_makes_room_for_its_max()
    {
        var yaml = ComposeEditor.SetAutoscale(Web, "web", new AutoscaleSpec(4, 70, [], true), ComposeEditor.PortsRange);
        Assert.Equal(["8765-8768:8080", "9000"], Svc(yaml, "web").PortList!);
    }

    [Fact]
    public void Deploy_puts_a_balancer_on_the_fixed_ports()
    {
        var yaml = ComposeEditor.SetLimits(Web, "web", new L(3, null, null, ComposeEditor.PortsBalance));
        var split = ComposeSplitter.Split(new SplitInput(yaml, 1, new Dictionary<string, int> { ["alpha"] = 1 }, new Dictionary<int, string> { [1] = "alpha" },
            new Dictionary<int, string?> { [1] = null }, new HashSet<int> { 1 }));
        Assert.Empty(split.Problems);
        var sent = split.Hosts.Single().ComposeYaml;
        var web = Service(sent, "web");
        Assert.Equal(["9000"], List(web, "ports"));
        var lb = Service(sent, "web-balancer");
        Assert.Equal(PortBalancer.Image, Text(lb, "image"));
        Assert.Equal(["8765:8765"], List(lb, "ports"));
        Assert.Equal("always", Text(lb, "restart"));
        Assert.Equal(["web"], List(lb, "depends_on"));
        var cfg = Text(lb, "environment", "DM_HAPROXY_CFG")!;
        Assert.Contains("bind :8765", cfg);
        Assert.Contains("server-template r 1-100 web:8080 check", cfg);
        // the summary of what is sent: the service can now run side by side
        Assert.Equal((0, 3), (Svc(sent, "web").FixedPorts, Svc(sent, "web").Replicas));
    }

    [Fact]
    public void The_balancer_is_on_the_services_networks()
    {
        var yaml = "services:\n  web:\n    image: a\n    deploy:\n      replicas: 2\n    ports: [\"80:80\"]\n    networks:\n      front:\n        aliases: [w]\n    x-dockmaster:\n      balance: true\nnetworks:\n  front: {}\n";
        var root = Root(yaml);
        PortBalancer.Apply(root);
        Assert.Equal(["front"], List(root, "services", "web-balancer", "networks"));
    }

    [Fact]
    public void A_deploy_or_restart_of_the_service_takes_its_balancer_along()
    {
        var root = Root(ComposeEditor.SetLimits(Web, "web", new L(2, null, null, ComposeEditor.PortsBalance)));
        PortBalancer.Apply(root);
        var sent = Save(root);
        Assert.Equal(["web", "web-balancer"], PortBalancer.WithBalancers(["web"], sent));
        Assert.Null(PortBalancer.WithBalancers(null, sent));
        Assert.Equal(["web"], PortBalancer.WithBalancers(["web"], Web));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("\n      rolling: true", true)]
    [InlineData("\n      autoscale:\n        mode: auto\n        max: 3", true)]
    [InlineData("\n      autoscale:\n        mode: recommend\n        max: 3", false)]
    public void Back_at_one_replica_the_balancer_goes_unless_something_needs_it(string extra, bool balancer)
    {
        var yaml = ComposeEditor.SetLimits(ComposeEditor.SetLimits(Web, "web", new L(2, null, null, ComposeEditor.PortsBalance)), "web", new L(1, null, null, null));
        yaml = yaml.Replace("\r\n", "\n").Replace("\n      balance: true", "\n      balance: true" + extra);
        var split = ComposeSplitter.Split(new SplitInput(yaml, 1, new Dictionary<string, int> { ["alpha"] = 1 }, new Dictionary<int, string> { [1] = "alpha" },
            new Dictionary<int, string?> { [1] = null }, new HashSet<int> { 1 }));
        Assert.Empty(split.Problems);
        var sent = split.Hosts.Single().ComposeYaml;
        Assert.Equal(balancer, At(Root(sent), "services", "web-balancer") != null);
        // the choice stays for the next time it has more replicas
        Assert.True(Svc(yaml, "web").Balance);
        Assert.Equal(balancer ? ["9000"] : ["8765:8080", "9000"], List(Service(sent, "web"), "ports"));
        Assert.Equal(balancer ? ["web", "web-balancer"] : ["web"], PortBalancer.WithBalancers(["web"], sent));
    }

    [Theory]
    [InlineData("    ports: [\"53:53/udp\"]", "UDP")]
    [InlineData("    ports: [\"80:80\"]\n    network_mode: host", "network_mode")]
    [InlineData("    ports: [\"${PORT}:80\"]", "not a plain number")]
    public void What_stops_a_balancer_stops_the_deploy(string ports, string problem)
    {
        var yaml = $"services:\n  web:\n    image: a\n{ports}\n    x-dockmaster:\n      balance: true\n";
        var split = ComposeSplitter.Split(new SplitInput(yaml, 1, new Dictionary<string, int> { ["alpha"] = 1 }, new Dictionary<int, string> { [1] = "alpha" },
            new Dictionary<int, string?> { [1] = null }, new HashSet<int> { 1 }));
        Assert.Contains(split.Problems, p => p.Contains(problem));
    }
}
