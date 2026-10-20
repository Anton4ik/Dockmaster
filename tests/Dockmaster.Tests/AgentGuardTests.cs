using Dockmaster.Agent.Docker;

namespace Dockmaster.Tests;

/// <summary>The agent and its mesh sidecar get no terminal and are not stopped from the UI.</summary>
public class AgentGuardTests
{
    private const string Self = "3f2a9c0d1e4b5a6978c0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8d9e0f1a2";

    [Fact]
    public void The_agent_and_the_mesh_sidecar_are_guarded()
    {
        Assert.NotNull(DockerService.Guarded(new ContainerInspectResponse { ID = Self, Name = "/dockmaster-agent" }, Self));
        // SelfId falls back to the short hostname when mountinfo cannot be read
        Assert.NotNull(DockerService.Guarded(new ContainerInspectResponse { ID = Self, Name = "/renamed" }, Self[..12]));
        Assert.NotNull(DockerService.Guarded(new ContainerInspectResponse { ID = "ab" + Self[2..], Name = "/dockmaster-mesh" }, Self));
    }

    [Fact]
    public void Other_containers_are_not()
    {
        Assert.Null(DockerService.Guarded(new ContainerInspectResponse { ID = "ab" + Self[2..], Name = "/shop-web-1" }, Self));
        // an agent outside a container has no container of its own
        Assert.Null(DockerService.Guarded(new ContainerInspectResponse { ID = Self, Name = "/dockmaster-agent" }, null));
    }
}
