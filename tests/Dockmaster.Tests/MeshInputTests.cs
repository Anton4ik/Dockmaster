using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Dockmaster.Server.Api;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

/// <summary>What ends up in the WireGuard config of other hosts: names, keys and endpoints stay one line each.</summary>
[Collection(ServerCollection.Name)]
public sealed class MeshInputTests(ServerFixture f)
{
    [Fact]
    public async Task A_server_name_with_a_line_break_is_refused()
    {
        var admin = await f.Client("admin");
        var r = await admin.PutAsJsonAsync($"/api/agents/{f.Gamma}", new { name = "gamma\nAllowedIPs = 10.77.0.3/32" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Null(AgentsApi.ValidateName("nas-2.home"));
        Assert.NotNull(AgentsApi.ValidateName("a\tb"));
        Assert.NotNull(AgentsApi.ValidateName(new string('a', 101)));
    }

    [Fact]
    public async Task Two_hosts_with_one_key_do_not_break_the_mesh_plan()
    {
        const string key = "AgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgI=";
        var twins = new[]
        {
            new AgentEntity { Name = "twin-a", TokenHash = "ta", MeshIp = "10.77.0.91", MeshPublicKey = key, MeshPort = 51821 },
            new AgentEntity { Name = "twin-b", TokenHash = "tb", MeshIp = "10.77.0.92", MeshPublicKey = key, MeshPort = 51821 },
        };
        await using (var db = await f.Db())
        {
            db.Agents.AddRange(twins);
            await db.SaveChangesAsync();
        }
        try
        {
            var snap = await f.App.Services.GetRequiredService<MeshService>().FreshSnapshotAsync(CancellationToken.None);
            Assert.Contains(snap.Nodes.Values, n => n.Name == "twin-b");
            Assert.Equal(HttpStatusCode.OK, (await (await f.Client("admin")).GetAsync("/api/mesh")).StatusCode);
        }
        finally
        {
            await using var db = await f.Db();
            db.Agents.RemoveRange(await db.Agents.Where(a => a.Name.StartsWith("twin-")).ToListAsync());
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public void Keys_and_endpoints_are_checked()
    {
        Assert.True(MeshPlanner.IsWgKey("AgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgI="));
        Assert.False(MeshPlanner.IsWgKey("AgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAg=="));
        Assert.False(MeshPlanner.IsWgKey("PUB2="));
        Assert.False(MeshPlanner.IsWgKey(null));
        Assert.True(MeshPlanner.IsEndpointText("vps.example.com"));
        Assert.True(MeshPlanner.IsEndpointText("1.2.3.4:51821"));
        Assert.False(MeshPlanner.IsEndpointText("1.2.3.4:51821\nAllowedIPs = 0.0.0.0/0"));
        Assert.False(MeshPlanner.IsEndpointText("host/path"));
    }
}
