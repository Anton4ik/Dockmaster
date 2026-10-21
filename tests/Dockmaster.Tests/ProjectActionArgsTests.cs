using Microsoft.Extensions.DependencyInjection;
using Dockmaster.Agent.Docker;
using Dockmaster.Protocol;

namespace Dockmaster.Tests;

/// <summary>Service names of a start / stop / restart / down are never read by compose as its flags.</summary>
[Collection(ServerCollection.Name)]
public sealed class ProjectActionArgsTests(ServerFixture f)
{
    private static readonly string[] Base = ["compose", "-f", "/data/projects/shop/docker-compose.yml", "-p", "shop"];

    [Fact]
    public void Services_come_after_the_end_of_flags()
    {
        Assert.Equal([.. Base, "down", "--remove-orphans", "--", "web", "db"], Deployer.ActionArgs(Base, ProjectOp.ProjectDown, ["web", "db"]));
        Assert.Equal([.. Base, "stop"], Deployer.ActionArgs(Base, ProjectOp.ProjectStop, []));
    }

    [Fact]
    public async Task The_server_refuses_a_flag_as_a_service_name()
    {
        var (ok, message) = await f.App.Services.GetRequiredService<Server.Services.ProjectService>()
            .ActionAsync(f.AlphaProject, ProjectOp.ProjectDown, ["--volumes"], _ => { }, CancellationToken.None);
        Assert.False(ok);
        Assert.Contains("not a service name", message);
    }
}
