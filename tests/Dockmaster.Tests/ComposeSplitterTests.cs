using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

public class ComposeSplitterTests
{
    private static readonly Dictionary<string, int> Ids = new() { ["alpha"] = 1, ["beta"] = 2, ["gamma"] = 3 };
    private static readonly Dictionary<int, string> Names = new() { [1] = "alpha", [2] = "beta", [3] = "gamma" };
    private static readonly Dictionary<int, string?> Mesh = new() { [1] = "10.77.0.1", [2] = "10.77.0.2", [3] = "10.77.0.3" };

    private static SplitInput Input(string yaml, Dictionary<int, string?>? mesh = null, HashSet<int>? online = null, Dictionary<string, IReadOnlyDictionary<int, int>>? extra = null) =>
        new(yaml, 1, Ids, Names, mesh ?? Mesh, online ?? [1, 2, 3], extra);

    private static ServiceSummary Svc(HostPlan host, string service) => ComposeServices.Parse(host.ComposeYaml).Single(s => s.Name == service);

    private const string TwoHosts = """
        services:
          web:
            image: nginx
            depends_on:
              - api
              - db
            networks: [front]
          api:
            image: app:1
            x-dockmaster:
              agent: beta
            depends_on:
              db:
                condition: service_healthy
            expose: [8080]
            volumes:
              - ./conf:/etc/app:ro
              - cache:/cache
            secrets: [api_key]
            networks:
              front:
              back:
          db:
            image: postgres:17
            expose:
              - "5432"
            volumes:
              - data:/var/lib/postgresql/data
            networks: [back]
        volumes:
          data:
          cache:
          unused:
        networks:
          front:
          back:
        secrets:
          api_key:
            file: ./key
          other:
            file: ./other
        """;

    [Fact]
    public void A_project_on_one_server_passes_through_untouched()
    {
        var yaml = "services:\n  web:\n    image: nginx # keep me\n  db:\n    image: postgres\n";
        var r = ComposeSplitter.Split(Input(yaml));
        Assert.False(r.Spanning);
        var host = Assert.Single(r.Hosts);
        Assert.Equal(1, host.AgentId);
        Assert.Same(yaml, host.ComposeYaml);
        Assert.Equal(["web", "db"], host.Services);
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void All_services_placed_elsewhere_still_run_as_one_host()
    {
        var yaml = "services:\n  web:\n    image: nginx\n    x-dockmaster:\n      agent: beta\n";
        var host = Assert.Single(ComposeSplitter.Split(Input(yaml)).Hosts);
        Assert.Equal(2, host.AgentId);
        Assert.Same(yaml, host.ComposeYaml);
    }

    [Fact]
    public void Splits_by_server_and_wires_the_exposed_services()
    {
        var r = ComposeSplitter.Split(Input(TwoHosts));
        Assert.True(r.Spanning);
        Assert.Empty(r.Problems);
        Assert.Equal(2, r.Hosts.Count);
        var alpha = r.Hosts.Single(h => h.AgentId == 1);
        var beta = r.Hosts.Single(h => h.AgentId == 2);
        Assert.Equal(["db", "web"], alpha.Services);
        Assert.Equal(["api"], beta.Services);

        Assert.DoesNotContain("\r", alpha.ComposeYaml);
        Assert.DoesNotContain("...", alpha.ComposeYaml);
        var a = ComposeServices.Parse(alpha.ComposeYaml).ToDictionary(s => s.Name);
        Assert.Equal(["db", "web"], a.Keys.Order());
        // db is published on alpha's mesh address only
        Assert.Contains("\"10.77.0.1:5432:5432\"", alpha.ComposeYaml);
        Assert.Equal(1, a["db"].Ports);
        // web (on alpha) reaches api (on beta) by name
        Assert.Contains("\"api:10.77.0.2\"", alpha.ComposeYaml);
        // web's depends_on keeps db, loses api
        Assert.Contains("depends_on:\n    - db\n", alpha.ComposeYaml);
        Assert.DoesNotContain("- api\n", alpha.ComposeYaml);
        // unused top-level declarations are gone, used ones stay
        Assert.Contains("data:", alpha.ComposeYaml);
        Assert.DoesNotContain("cache:", alpha.ComposeYaml);
        Assert.DoesNotContain("unused:", alpha.ComposeYaml);
        Assert.DoesNotContain("secrets:", alpha.ComposeYaml);
        Assert.Contains("front:", alpha.ComposeYaml);
        Assert.Contains("back:", alpha.ComposeYaml);

        Assert.Contains("\"10.77.0.2:8080:8080\"", beta.ComposeYaml);
        Assert.Contains("\"db:10.77.0.1\"", beta.ComposeYaml);
        Assert.DoesNotContain("depends_on", beta.ComposeYaml); // db is remote, the map became empty
        Assert.Contains("cache:", beta.ComposeYaml);
        Assert.DoesNotContain("data:", beta.ComposeYaml);
        Assert.Contains("api_key:", beta.ComposeYaml);
        Assert.DoesNotContain("other:", beta.ComposeYaml);
        Assert.Contains("- ./conf:/etc/app:ro", beta.ComposeYaml); // bind mounts stay as they are

        Assert.Equal(["5432"], alpha.Exposed.Single().Ports);
        Assert.Equal("10.77.0.1", alpha.Exposed.Single().Address);
        Assert.Contains(r.Warnings, w => w.StartsWith("api depends on db, which runs on alpha"));
        Assert.Contains(r.Warnings, w => w.StartsWith("web depends on api, which runs on beta"));
    }

    [Fact]
    public void Existing_ports_and_extra_hosts_are_kept()
    {
        var yaml = """
            services:
              web:
                image: nginx
                ports:
                  - "80:80"
                extra_hosts:
                  - "legacy:192.168.1.9"
              db:
                image: postgres
                x-dockmaster: {agent: beta}
                expose: ["5432"]
            """;
        var r = ComposeSplitter.Split(Input(yaml));
        var alpha = r.Hosts.Single(h => h.AgentId == 1);
        Assert.Contains("\"80:80\"", alpha.ComposeYaml);
        Assert.Contains("\"legacy:192.168.1.9\"", alpha.ComposeYaml);
        Assert.Contains("\"db:10.77.0.2\"", alpha.ComposeYaml);
        Assert.Equal(1, ComposeServices.Parse(alpha.ComposeYaml).Single().Ports);
    }

    [Fact]
    public void Extra_hosts_as_a_mapping_gets_a_key()
    {
        var yaml = "services:\n  web:\n    image: nginx\n    extra_hosts:\n      legacy: 192.168.1.9\n  db:\n    image: postgres\n    x-dockmaster: {agent: beta}\n    expose: [5432]\n";
        var alpha = ComposeSplitter.Split(Input(yaml)).Hosts.Single(h => h.AgentId == 1);
        Assert.Contains("db: 10.77.0.2", alpha.ComposeYaml);
        Assert.Contains("legacy: 192.168.1.9", alpha.ComposeYaml);
    }

    [Fact]
    public void Reports_what_stops_a_deploy()
    {
        var yaml = """
            services:
              web:
                image: nginx
                x-dockmaster: {agent: nowhere}
              api:
                image: app
                x-dockmaster: {agent: beta}
                expose: [8080, "8000-8010", "bad"]
                deploy: {replicas: 3}
              db:
                image: postgres
                x-dockmaster: {agent: gamma}
                expose: [5432]
              db2:
                image: postgres
                expose: [5432]
                x-dockmaster: {agent: gamma, rolling: true}
            """;
        var r = ComposeSplitter.Split(Input(yaml, new Dictionary<int, string?> { [1] = "10.77.0.1", [2] = "10.77.0.2", [3] = null }, [1, 3]));
        Assert.Contains("service web: server 'nowhere' does not exist", r.Problems);
        Assert.Contains(r.Problems, p => p.StartsWith("service api: expose entry '8000-8010'"));
        Assert.Contains(r.Problems, p => p.StartsWith("service api: expose entry 'bad'"));
        // several replicas of an exposed service are fine now: a balancer reaches them
        Assert.DoesNotContain(r.Problems, p => p.StartsWith("service api is reachable"));
        Assert.Contains(r.Problems, p => p.StartsWith("server gamma is not in the mesh"));
        Assert.Contains(r.Problems, p => p.StartsWith("server beta is offline"));
        Assert.Contains(r.Problems, p => p.Contains("db and db2 both need port 5432"));
        Assert.Contains(r.Problems, p => p.StartsWith("service db2 is reachable from other servers") && p.Contains("rolling"));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    public void The_rolling_flag_is_read_like_the_agent_reads_it(string flag, bool refused)
    {
        var yaml = $"services:\n  web:\n    image: nginx\n  db:\n    image: postgres\n    expose: [5432]\n    x-dockmaster: {{agent: beta, rolling: {flag}}}\n";
        var r = ComposeSplitter.Split(Input(yaml));
        Assert.Equal(refused, r.Problems.Any(p => p.Contains("rolling update is not possible")));
        Assert.Equal(bool.Parse(flag), ComposeServices.Parse(yaml).Single(s => s.Name == "db").RollingFlag);
    }

    private const string Spread = """
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
          db:
            image: postgres:17
            expose: [5432]
            x-dockmaster: {agent: gamma}
        """;

    [Fact]
    public void Replicas_are_spread_over_the_listed_servers_the_first_ones_taking_the_rest()
    {
        var r = ComposeSplitter.Split(Input(Spread));
        Assert.Empty(r.Problems);
        var alpha = r.Hosts.Single(h => h.AgentName == "alpha");
        var beta = r.Hosts.Single(h => h.AgentName == "beta");
        Assert.Equal(2, alpha.Replicas["api"]);
        Assert.Equal(1, beta.Replicas["api"]);
        Assert.Equal(2, Svc(alpha, "api").Replicas);
        Assert.Equal(1, Svc(beta, "api").Replicas);
        Assert.DoesNotContain(r.Hosts, h => h.AgentName == "gamma" && h.Services.Contains("api"));
    }

    [Fact]
    public void Several_replicas_are_published_on_ports_docker_picks_and_reached_through_a_balancer_everywhere()
    {
        var r = ComposeSplitter.Split(Input(Spread));
        var alpha = r.Hosts.Single(h => h.AgentName == "alpha");
        var gamma = r.Hosts.Single(h => h.AgentName == "gamma");
        // each replica on a port of its own on the mesh address
        Assert.Contains("10.77.0.1::8080", alpha.ComposeYaml);
        Assert.True(Assert.Single(alpha.Exposed).Balanced);
        // gamma runs no api: its services reach api through gamma's own balancer
        var f = Assert.Single(gamma.Frontends);
        Assert.Equal(("api", "10.77.0.3"), (f.Service, f.Address));
        Assert.Contains("api:10.77.0.3", gamma.ComposeYaml);
        // alpha runs api itself, but api is spread: alpha balances too, so beta's replica gets its share from alpha;
        // api does not point at itself
        Assert.Equal(("api", "10.77.0.1"), (Assert.Single(alpha.Frontends).Service, alpha.Frontends[0].Address));
        Assert.DoesNotContain("api:", alpha.ComposeYaml.Split("extra_hosts")[^1]);
        // db (one replica) keeps its port
        Assert.Contains("10.77.0.3:5432:5432", gamma.ComposeYaml);
        Assert.Contains("db:10.77.0.3", alpha.ComposeYaml);
    }

    [Fact]
    public void Balancing_is_refused_for_udp_and_a_balancer_may_not_take_a_port_already_held()
    {
        var udp = Spread.Replace("expose: [8080]", "expose: [8080, 53/udp]");
        Assert.Contains(ComposeSplitter.Split(Input(udp)).Problems, p => p.Contains("UDP port 53/udp cannot be balanced"));
        // db on gamma holds 5432 on gamma's mesh address; a balancer there for another 5432 would clash
        var clash = Spread.Replace("expose: [8080]", "expose: [5432]");
        Assert.Contains(ComposeSplitter.Split(Input(clash)).Problems, p => p.StartsWith("server gamma: services") && p.Contains("5432"));
    }

    [Fact]
    public void Fewer_replicas_than_servers_leave_the_last_ones_without_and_say_so()
    {
        var one = Spread.Replace("replicas: 3", "replicas: 1");
        var r = ComposeSplitter.Split(Input(one));
        Assert.Contains(r.Warnings, w => w.StartsWith("service api: 1 replica(s) over 2 servers") && w.EndsWith("beta get none"));
        Assert.DoesNotContain(r.Hosts, h => h.AgentName == "beta");
        // one replica on one server: it holds its port, no balancer
        Assert.Contains("10.77.0.1:8080:8080", r.Hosts.Single(h => h.AgentName == "alpha").ComposeYaml);
        Assert.Empty(r.Hosts.SelectMany(h => h.Frontends));
    }

    [Fact]
    public void Services_on_a_server_with_replicas_of_a_spread_service_reach_it_through_the_balancer_there()
    {
        // the worker moves to alpha, next to two api replicas
        var yaml = Spread.Replace("image: worker:1\n    x-dockmaster: {agent: gamma}", "image: worker:1");
        var r = ComposeSplitter.Split(Input(yaml));
        Assert.Empty(r.Problems);
        var alpha = r.Hosts.Single(h => h.AgentName == "alpha");
        Assert.Contains("worker", alpha.Services);
        Assert.Contains("api:10.77.0.1", alpha.ComposeYaml);
        Assert.Contains(alpha.Frontends, f => f.Service == "api");
    }

    private const string Autoscaled = """
        services:
          api:
            image: app:1
            expose: [8080]
            deploy:
              replicas: 2
            x-dockmaster:
              autoscale:
                max: 4
                agents: [alpha, beta]
                mode: auto
          web:
            image: nginx
        """;

    [Fact]
    public void A_service_the_autoscaler_may_spread_is_balanced_from_the_start()
    {
        var r = ComposeSplitter.Split(Input(Autoscaled));
        Assert.Empty(r.Problems);
        // everything still on alpha, but laid out for several servers: nothing changes there when beta gets a replica
        var alpha = Assert.Single(r.Hosts);
        Assert.True(r.Spanning);
        Assert.Contains("10.77.0.1::8080", alpha.ComposeYaml);
        Assert.Contains("api:10.77.0.1", alpha.ComposeYaml);
        Assert.Equal("api", Assert.Single(alpha.Frontends).Service);

        var scaled = ComposeSplitter.Split(Input(Autoscaled, extra: new() { ["api"] = new Dictionary<int, int> { [2] = 1 } }));
        Assert.Equal(alpha.ComposeYaml, scaled.Hosts.Single(h => h.AgentName == "alpha").ComposeYaml);
        var beta = scaled.Hosts.Single(h => h.AgentName == "beta");
        Assert.Equal(1, beta.Replicas["api"]);
        Assert.Equal(["api"], beta.Services);
    }

    [Fact]
    public void Autoscaler_replicas_stay_within_max_and_the_allowed_servers()
    {
        var r = ComposeSplitter.Split(Input(Autoscaled, extra: new() { ["api"] = new Dictionary<int, int> { [3] = 1, [2] = 5 } }));
        // gamma is not allowed; beta gets only what max leaves (4 - 2)
        Assert.DoesNotContain(r.Hosts, h => h.AgentName == "gamma");
        Assert.Equal(2, r.Hosts.Single(h => h.AgentName == "beta").Replicas["api"]);
    }

    [Fact]
    public void Autoscaler_replicas_on_the_same_server_change_only_the_count()
    {
        var yaml = Autoscaled.Replace("agents: [alpha, beta]", "agents: [alpha]");
        var plain = Assert.Single(ComposeSplitter.Split(Input(yaml)).Hosts);
        Assert.Same(yaml, plain.ComposeYaml);
        var host = Assert.Single(ComposeSplitter.Split(Input(yaml, extra: new() { ["api"] = new Dictionary<int, int> { [1] = 1 } })).Hosts);
        Assert.Equal(3, Svc(host, "api").Replicas);
        Assert.Equal(3, host.Replicas["api"]);
    }

    [Fact]
    public void Recommend_mode_changes_nothing()
    {
        var yaml = Autoscaled.Replace("mode: auto", "mode: recommend");
        var r = ComposeSplitter.Split(Input(yaml, extra: new() { ["api"] = new Dictionary<int, int> { [2] = 1 } }));
        Assert.False(r.Spanning);
        Assert.Same(yaml, Assert.Single(r.Hosts).ComposeYaml);
        var spec = ComposeServices.Parse(yaml).Single(s => s.Name == "api").Autoscale!;
        Assert.Equal((4, 70, false), (spec.Max, spec.Cpu, spec.Auto));
    }

    [Fact]
    public void Autoscaler_servers_must_be_in_the_mesh()
    {
        var r = ComposeSplitter.Split(Input(Autoscaled, mesh: new() { [1] = "10.77.0.1", [2] = null, [3] = "10.77.0.3" }));
        Assert.Contains(r.Problems, p => p.StartsWith("server beta is not in the mesh"));
    }

    [Fact]
    public void Autoscaler_replicas_on_the_only_server_of_a_service_are_written_into_its_file()
    {
        // db runs on beta only and may only grow there; the project spans alpha (web) and beta
        const string yaml = """
            services:
              web:
                image: nginx
              db:
                image: app:1
                expose: [5432]
                x-dockmaster:
                  agent: beta
                  autoscale: {max: 3, mode: auto}
            """;
        var plain = ComposeSplitter.Split(Input(yaml));
        var scaled = ComposeSplitter.Split(Input(yaml, extra: new() { ["db"] = new Dictionary<int, int> { [2] = 1 } }));
        var beta = scaled.Hosts.Single(h => h.AgentName == "beta");
        Assert.Equal(2, Svc(beta, "db").Replicas);
        // balanced from the start: one replica more changes neither db's ports nor how alpha reaches it
        Assert.Contains("10.77.0.2::5432", plain.Hosts.Single(h => h.AgentName == "beta").ComposeYaml);
        Assert.Equal(plain.Hosts.Single(h => h.AgentName == "alpha").ComposeYaml, scaled.Hosts.Single(h => h.AgentName == "alpha").ComposeYaml);
        Assert.Contains("10.77.0.2::5432", beta.ComposeYaml);
    }

    [Theory]
    [InlineData("\"8080:80\"", true)]
    [InlineData("\"0.0.0.0:8080:80\"", true)]
    [InlineData("\"10.77.0.1:8080:80\"", true)]
    [InlineData("\"192.168.1.5:8080:80\"", false)]
    [InlineData("\"8080:80/udp\"", false)]
    [InlineData("\"80\"", false)]
    [InlineData("{published: 8080, target: 80}", true)]
    public void A_balancer_may_not_take_a_port_a_service_there_publishes_on_the_host(string port, bool clash)
    {
        // api is spread over alpha and beta, so alpha balances it on 10.77.0.1:8080; proxy publishes a port on alpha
        var yaml = Spread.Replace("  worker:\n    image: worker:1\n    x-dockmaster: {agent: gamma}", $"  proxy:\n    image: caddy\n    ports:\n      - {port}");
        var problems = ComposeSplitter.Split(Input(yaml)).Problems;
        Assert.Equal(clash, problems.Any(p => p.StartsWith("server alpha: the balancer for api needs port 8080")));
    }

    [Fact]
    public void Unparsable_yaml_goes_to_the_default_server_for_compose_to_complain()
    {
        var yaml = "services: [not: a: mapping";
        var r = ComposeSplitter.Split(Input(yaml));
        var host = Assert.Single(r.Hosts);
        Assert.Equal(1, host.AgentId);
        Assert.Same(yaml, host.ComposeYaml);
    }
}
