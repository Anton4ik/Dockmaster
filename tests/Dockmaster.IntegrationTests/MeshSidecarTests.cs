using Dockmaster.Agent.Docker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dockmaster.IntegrationTests;

[Collection("docker")]
public sealed class MeshSidecarTests(DockerFixture d)
{
    /// <summary>
    /// The sidecar is created with what the mesh needs from the host. The test image has no dm-mesh, so it cannot
    /// start: what docker made of the create call is still there to look at.
    /// </summary>
    [DockerFact]
    public async Task The_sidecar_gets_host_network_net_admin_and_its_data_dir()
    {
        if ((await DockerFixture.Cli("inspect", MeshSidecar.Name)).code == 0) return; // a real mesh on this host: leave it alone
        var dir = Path.Combine(DockerFixture.DataDir, "mesh-" + d.Prefix);
        var sidecar = new MeshSidecar(d.Get<DockerService>(), dir, NullLogger.Instance);
        try
        {
            var error = await Record.ExceptionAsync(() => sidecar.EnsureAsync(null, default));
            Assert.NotNull(error);
            Assert.Contains("dm-mesh", error.Message);
            var c = await DockerFixture.Inspect(MeshSidecar.Name);
            var cfg = c.GetProperty("Config");
            Assert.Equal(["dm-mesh", "run"], cfg.GetProperty("Entrypoint").EnumerateArray().Select(x => x.GetString()));
            Assert.Contains("DM_MESH_DIR=" + dir, cfg.GetProperty("Env").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal(dir, cfg.GetProperty("Labels").GetProperty("dockmaster.mesh.dir").GetString());
            Assert.Equal("1", cfg.GetProperty("Labels").GetProperty("dockmaster.mesh").GetString());
            var h = c.GetProperty("HostConfig");
            Assert.Equal("host", h.GetProperty("NetworkMode").GetString());
            Assert.Equal(["NET_ADMIN"], h.GetProperty("CapAdd").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal([$"{dir}:{dir}"], h.GetProperty("Binds").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal("always", h.GetProperty("RestartPolicy").GetProperty("Name").GetString());
            Assert.Equal(128L << 20, h.GetProperty("Memory").GetInt64());
            d.Snapshot("mesh-hostconfig.json", h.GetRawText().Replace(dir, "<dir>"));
        }
        finally
        {
            await DockerFixture.Cli("rm", "-f", MeshSidecar.Name);
        }
    }
}
