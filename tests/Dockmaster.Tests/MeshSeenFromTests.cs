using Dockmaster.Protocol;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

public class MeshSeenFromTests
{
    private static AgentEntity Agent(int id, string key) => new() { Id = id, Name = $"s{id}", TokenHash = $"t{id}", MeshIp = $"10.77.0.{id}", MeshPublicKey = key };

    private static MeshPeerStatus Saw(string key, string endpoint) => new() { PublicKey = key, Endpoint = endpoint };

    [Fact]
    public void Only_planned_peers_say_where_a_host_is_seen_from()
    {
        // 1 is public and peers with 2 and 3; 2 and 3 are behind NATs and only peer with 1
        var agents = new List<AgentEntity> { Agent(1, "K1"), Agent(2, "K2"), Agent(3, "K3"), Agent(4, "K4"), Agent(5, "K4") };
        var planned = new Dictionary<int, IReadOnlySet<string>>
        {
            [1] = new HashSet<string> { "K2", "K3" },
            [2] = new HashSet<string> { "K1" },
            [3] = new HashSet<string> { "K1" },
        };
        var reports = new Dictionary<int, MeshPeerStatus[]>
        {
            [1] = [Saw("K2", "203.0.113.7:51821"), Saw("K3", "192.168.1.5:51821")],
            // 3 has no business telling where 2 is, nor where it is itself
            [3] = [Saw("K2", "198.51.100.66:51821"), Saw("K3", "198.51.100.66:51821"), Saw("K1", "198.51.100.1:51821")],
            // a key two hosts share says nothing about either
            [2] = [Saw("K4", "198.51.100.9:51821")],
        };
        var seen = MeshService.SeenFrom(agents, id => reports.GetValueOrDefault(id), id => planned.GetValueOrDefault(id) ?? new HashSet<string>()).ToList();
        // 3's address is private (the LAN): not a NAT's
        Assert.Equal([(2, "K2", "203.0.113.7"), (1, "K1", "198.51.100.1")], seen);
    }
}
