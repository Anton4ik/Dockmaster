using Dockmaster.Protocol;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

public class ProjectPlanTests
{
    private static SplitResult Spanning() => new([
        new HostPlan(1, "vps", ["web"], "", []),
        new HostPlan(2, "mediastack", ["db"], "", [])], [], []);

    [Fact]
    public void A_same_named_project_on_a_server_never_deployed_to_blocks_the_deploy()
    {
        // what happened in the live test: "demo" placed on a host that already ran another "demo" (nginx, 2 replicas)
        var project = new ProjectEntity { Name = "demo", AgentId = 1 };
        var containers = new Dictionary<int, IReadOnlyList<ContainerInfo>>
        {
            [1] = [],
            [2] = [new() { Name = "demo-web-1", Project = "demo" }, new() { Name = "demo-web-2", Project = "demo" }, new() { Name = "plex", Project = "" }],
        };
        var problem = Assert.Single(ProjectService.ForeignProjectProblems(project, Spanning(), containers));
        Assert.StartsWith("server mediastack already runs containers of another compose project named 'demo' (demo-web-1, demo-web-2)", problem);
        // once this project has been deployed there, the containers are its own
        project.DeployedAgentIds = "1,2";
        Assert.Empty(ProjectService.ForeignProjectProblems(project, Spanning(), containers));
        // a project on one server keeps the import take-over
        project.DeployedAgentIds = "";
        Assert.Empty(ProjectService.ForeignProjectProblems(project, new SplitResult([new HostPlan(2, "mediastack", ["web"], "", [])], [], []), containers));
    }
}
