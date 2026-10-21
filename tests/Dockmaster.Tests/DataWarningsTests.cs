using Dockmaster.Protocol;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

public class DataWarningsTests
{
    private static readonly Dictionary<string, int> Ids = new() { ["alpha"] = 1, ["beta"] = 2 };
    private static readonly Dictionary<int, string> Names = new() { [1] = "alpha", [2] = "beta" };
    private static readonly Dictionary<int, string?> Mesh = new() { [1] = "10.77.0.1", [2] = "10.77.0.2" };

    private static List<string> Warnings(string yaml, Dictionary<int, IReadOnlyList<ContainerInfo>>? running = null)
    {
        var project = new ProjectEntity { Name = "shop", AgentId = 1, ComposeYaml = yaml };
        var split = ComposeSplitter.Split(new SplitInput(yaml, 1, Ids, Names, Mesh, new HashSet<int> { 1, 2 }));
        return ProjectService.DataWarnings(project, split, ComposeServices.Parse(yaml, "shop"), running ?? [], Names).ToList();
    }

    [Fact]
    public void A_service_that_moves_leaves_its_data_behind()
    {
        const string yaml = "services:\n  db:\n    image: postgres\n    x-dockmaster: {agent: beta}\n    volumes:\n      - pgdata:/var/lib/postgresql/data\nvolumes:\n  pgdata: {}\n";
        var running = new Dictionary<int, IReadOnlyList<ContainerInfo>> { [1] = [new() { Project = "shop", Service = "db", Name = "shop-db-1" }], [2] = [] };
        var w = Assert.Single(Warnings(yaml, running));
        Assert.StartsWith("service db moves from alpha to beta: its data (pgdata) stays on alpha", w);
        // before it ever ran, or once it runs on beta, nothing moves
        Assert.Empty(Warnings(yaml));
    }

    [Fact]
    public void Replicas_spread_over_servers_each_have_their_own_copy()
    {
        const string yaml = "services:\n  web:\n    image: nginx\n    deploy: {replicas: 2}\n    x-dockmaster: {agents: [alpha, beta]}\n    volumes:\n      - ./html:/usr/share/nginx/html:ro\n      - /var/run/docker.sock:/var/run/docker.sock\n";
        Assert.StartsWith("service web keeps its data in ./html: each of alpha, beta has its own copy", Assert.Single(Warnings(yaml)));
    }

    [Fact]
    public void Unnamed_volumes_and_a_database_rolled_side_by_side_are_flagged()
    {
        const string yaml = "services:\n  db:\n    image: postgres\n    x-dockmaster: {rolling: true}\n    volumes:\n      - /var/lib/postgresql/data\n      - ./dump:/dump\n";
        var w = Warnings(yaml);
        Assert.Equal(2, w.Count);
        Assert.Contains("/var/lib/postgresql/data is an unnamed volume", w[0]);
        Assert.Contains("side by side on the same data (./dump)", w[1]);
    }
}
