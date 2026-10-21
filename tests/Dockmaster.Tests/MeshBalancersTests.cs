using Dockmaster.Agent.Docker;
using Dockmaster.Protocol;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

public class MeshBalancersTests
{
    private static readonly Dictionary<string, int> Ids = new(StringComparer.OrdinalIgnoreCase) { ["alpha"] = 1, ["beta"] = 2, ["gamma"] = 3 };
    private static readonly Dictionary<int, string> Names = new() { [1] = "alpha", [2] = "beta", [3] = "gamma" };
    private static readonly Dictionary<int, string?> Mesh = new() { [1] = "10.77.0.1", [2] = "10.77.0.2", [3] = "10.77.0.3" };

    // api: 3 replicas over alpha and beta; gamma runs only the worker, so it reaches api through a balancer
    private const string Yaml = """
        services:
          api:
            image: app:1
            expose: [8080]
            deploy:
              replicas: 3
            x-dockmaster:
              agents: [alpha, beta]
          worker:
            image: worker:1
            x-dockmaster: {agent: gamma}
        """;

    private static ContainerInfo Replica(string service, string state, params string[] ports)
    {
        var c = new ContainerInfo { Project = "shop", Service = service, State = state };
        c.Ports.AddRange(ports);
        return c;
    }

    private static Dictionary<int, IReadOnlyList<MeshFrontend>> Plan(Dictionary<int, IReadOnlyList<ContainerInfo>> containers, string yaml = Yaml) =>
        MeshBalancers.Plan([new ProjectEntity { Id = 7, Name = "shop", AgentId = 1, ComposeYaml = yaml }], Ids, Names, Mesh, new HashSet<int> { 1, 2, 3 }, containers);

    [Fact]
    public void A_server_without_replicas_balances_over_the_running_replicas_of_the_others()
    {
        var plan = Plan(new()
        {
            [1] = [Replica("api", "running", "10.77.0.1:32768->8080/tcp"), Replica("api", "running", "10.77.0.1:32769->8080/tcp", "9090/tcp")],
            // a stopped replica is not a backend; a port of another address (a user's own publish) is not either
            [2] = [Replica("api", "running", "10.77.0.2:32771->8080/tcp", "0.0.0.0:80->8080/tcp"), Replica("api", "exited", "10.77.0.2:32772->8080/tcp")],
            [3] = [Replica("worker", "running")],
        });
        var f = Assert.Single(plan[3]);
        Assert.Equal(("shop_api_8080", "10.77.0.3:8080"), (f.Name, f.Bind));
        Assert.Equal(["10.77.0.1:32768", "10.77.0.1:32769", "10.77.0.2:32771"], f.Backends);
        // alpha and beta run api themselves, but api is spread: they balance over every replica too, on their own address
        Assert.Equal(("10.77.0.1:8080", f.Backends), (Assert.Single(plan[1]).Bind, plan[1][0].Backends));
        Assert.Equal("10.77.0.2:8080", Assert.Single(plan[2]).Bind);
    }

    [Fact]
    public void An_older_agent_reports_ports_without_the_address_and_a_single_binding_is_taken()
    {
        var plan = Plan(new() { [1] = [Replica("api", "running", "32768->8080/tcp")], [2] = [Replica("api", "running", "32770->8080/tcp", "80->8080/tcp")], [3] = [] });
        // beta's replica has two bindings of 8080 and neither names its address: which one is the mesh one is unknown
        Assert.Equal(["10.77.0.1:32768"], Assert.Single(plan[3]).Backends);
    }

    [Fact]
    public void Projects_on_one_server_have_no_balancers()
    {
        var single = "services:\n  api:\n    image: app:1\n    expose: [8080]\n    deploy:\n      replicas: 3\n";
        Assert.Empty(Plan(new() { [1] = [], [2] = [], [3] = [] }, single));
    }

    [Fact]
    public void Haproxy_config_has_one_frontend_and_backend_per_balancer_and_skips_anything_odd()
    {
        var req = new MeshApply
        {
            Balance =
            {
                new MeshFrontend { Name = "shop_api_8080", Bind = "10.77.0.3:8080", Backends = { "10.77.0.2:32771", "10.77.0.1:32768", "not an address" } },
                new MeshFrontend { Name = "bad name; rm -rf", Bind = "10.77.0.3:9000" },
            },
        };
        var cfg = MeshConfig.HaproxyConfig(req);
        Assert.Contains("frontend fe_shop_api_8080\n    bind 10.77.0.3:8080\n    default_backend be_shop_api_8080\n", cfg);
        Assert.Contains("backend be_shop_api_8080\n", cfg);
        Assert.Contains("    server r1 10.77.0.1:32768 check\n    server r2 10.77.0.2:32771 check\n", cfg);
        Assert.DoesNotContain("not an address", cfg);
        Assert.DoesNotContain("9000", cfg);
        Assert.Equal("", MeshConfig.HaproxyConfig(new MeshApply()));
    }

    [Fact]
    public void Balancers_are_part_of_the_config_hash()
    {
        var node = new MeshNode(3, "gamma", "KEY3=", "10.77.0.3", 51821, null, ["10.0.0.3"], null, null, false);
        var net = new MeshNetworkDef(1, "main", null, new HashSet<int> { 3 });
        var s = new SettingsService.MeshSettings("10.77.0.0/16", 51821, null);
        var none = MeshPlanner.Plan([node], [net], s).Configs[3];
        var one = MeshPlanner.Plan([node], [net], s, new Dictionary<int, IReadOnlyList<MeshFrontend>>
        {
            [3] = [new MeshFrontend { Name = "shop_api_8080", Bind = "10.77.0.3:8080", Backends = { "10.77.0.1:32768" } }],
        }).Configs[3];
        Assert.Single(one.Balance);
        Assert.NotEqual(none.ConfigHash, one.ConfigHash);
    }
}
