using Dockmaster.Protocol;
using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

public class MeshPlannerTests
{
    private static readonly SettingsService.MeshSettings Settings = new("10.77.0.0/16", 51821, "dm.example.com");

    /// <summary>All the nodes in one network (id 1), the user's hub choice optional.</summary>
    private static MeshPlan Plan(MeshNode[] nodes, int? hub = null) =>
        MeshPlanner.Plan(nodes, [new MeshNetworkDef(1, "main", hub, nodes.Select(n => n.AgentId).ToHashSet())], Settings);

    private static MeshNode Node(int id, string ip, string? peerIp, string[] hostIps, bool? publicOverride = null, string? endpoint = null, bool serverHost = false) =>
        new(id, $"s{id}", $"KEY{id}=", ip, 51821, peerIp, hostIps, publicOverride, endpoint, serverHost);

    private static MeshPeer Peer(MeshApply cfg, string meshIp) => cfg.Peers.Single(p => p.MeshIp == meshIp);

    [Fact]
    public void Two_public_hosts_peer_directly_without_keepalive()
    {
        var a = Node(1, "10.77.0.1", "1.1.1.1", ["1.1.1.1"]);
        var b = Node(2, "10.77.0.2", "2.2.2.2", ["2.2.2.2"]);
        var plan = Plan([a, b]);
        Assert.Empty(plan.Problems);
        Assert.Equal(PathKind.Direct, plan.Path(1, 2)!.Kind);
        var pb = Peer(plan.Configs[1], "10.77.0.2");
        Assert.Equal("2.2.2.2:51821", pb.Endpoint);
        Assert.Equal(0, pb.Keepalive);
        Assert.Equal(["10.77.0.2/32"], pb.AllowedIps);
        Assert.True(plan.Configs[1].Hub);
        Assert.Equal("10.77.0.1/16", plan.Configs[1].Address);
    }

    [Fact]
    public void A_host_behind_nat_keeps_a_keepalive_to_the_public_one()
    {
        var pub = Node(1, "10.77.0.1", "1.1.1.1", ["1.1.1.1"]);
        var nat = Node(2, "10.77.0.2", "9.9.9.9", ["192.168.1.5"]);
        var plan = Plan([pub, nat]);
        Assert.Empty(plan.Problems);
        Assert.Equal([1], plan.PublicIds);
        var toPub = Peer(plan.Configs[2], "10.77.0.1");
        Assert.Equal("1.1.1.1:51821", toPub.Endpoint);
        Assert.Equal(25, toPub.Keepalive);
        var toNat = Peer(plan.Configs[1], "10.77.0.2");
        Assert.Equal("", toNat.Endpoint);
        Assert.Equal(0, toNat.Keepalive);
        Assert.False(plan.Configs[2].Hub);
    }

    [Fact]
    public void Hosts_behind_the_same_nat_use_the_lan()
    {
        var a = Node(1, "10.77.0.1", "9.9.9.9", ["192.168.1.5"]);
        var b = Node(2, "10.77.0.2", "9.9.9.9", ["192.168.1.6", "172.17.0.1"]);
        var plan = Plan([a, b]);
        Assert.Empty(plan.Problems);
        Assert.Equal(PathKind.Lan, plan.Path(1, 2)!.Kind);
        Assert.Equal("192.168.1.6:51821", Peer(plan.Configs[1], "10.77.0.2").Endpoint);
        Assert.Equal("192.168.1.5:51821", Peer(plan.Configs[2], "10.77.0.1").Endpoint);
        Assert.Equal(25, Peer(plan.Configs[2], "10.77.0.1").Keepalive);
    }

    [Fact]
    public void Hosts_behind_different_nats_go_through_the_hub()
    {
        var hub = Node(1, "10.77.0.1", "1.1.1.1", ["1.1.1.1"]);
        var b = Node(2, "10.77.0.2", "8.8.8.8", ["192.168.1.5"]);
        var c = Node(3, "10.77.0.3", "9.9.9.9", ["10.0.0.5"]);
        var plan = Plan([hub, b, c]);
        Assert.Empty(plan.Problems);
        Assert.Equal(1, plan.Hubs[1]);
        Assert.Equal(PathKind.ViaHub, plan.Path(2, 3)!.Kind);
        Assert.Equal(1, plan.Path(2, 3)!.HubId);
        // b: one peer (the hub) that also routes c
        Assert.Single(plan.Configs[2].Peers);
        Assert.Equal(["10.77.0.1/32", "10.77.0.3/32"], Peer(plan.Configs[2], "10.77.0.1").AllowedIps);
        Assert.Equal(["10.77.0.1/32", "10.77.0.2/32"], Peer(plan.Configs[3], "10.77.0.1").AllowedIps);
        // the hub knows both by their own /32 only
        Assert.Equal(["10.77.0.2/32"], Peer(plan.Configs[1], "10.77.0.2").AllowedIps);
        Assert.True(plan.Configs[1].Hub);
    }

    [Fact]
    public void No_public_host_means_no_path_and_a_problem()
    {
        var b = Node(2, "10.77.0.2", "8.8.8.8", ["192.168.1.5"]);
        var c = Node(3, "10.77.0.3", "9.9.9.9", ["10.0.0.5"]);
        var plan = Plan([b, c]);
        Assert.Equal(PathKind.None, plan.Path(2, 3)!.Kind);
        Assert.Contains(plan.Problems, p => p.StartsWith("main: no way between s2 and s3"));
        Assert.Empty(plan.Configs[2].Peers);
    }

    [Fact]
    public void A_public_address_on_the_host_counts_even_when_the_connection_hides_it()
    {
        // seen live: docker-proxy on the server's host makes every agent connection come from 172.17.0.1
        var vps = Node(1, "10.77.0.1", "172.17.0.1", ["173.249.0.192", "172.17.0.1"]);
        var home = Node(2, "10.77.0.2", "172.17.0.1", ["192.168.68.42"]);
        var plan = Plan([vps, home]);
        Assert.Equal([1], plan.PublicIds);
        Assert.Equal("173.249.0.192:51821", Peer(plan.Configs[2], "10.77.0.1").Endpoint);
        Assert.Equal(25, Peer(plan.Configs[2], "10.77.0.1").Keepalive);
    }

    [Fact]
    public void A_shared_private_subnet_alone_is_a_lan_only_without_a_hub()
    {
        // two homes on 192.168.1.x look alike: with a hub around, go through it rather than guess
        var hub = Node(1, "10.77.0.1", null, ["1.1.1.1"]);
        var a = Node(2, "10.77.0.2", "172.17.0.1", ["192.168.1.5"]);
        var b = Node(3, "10.77.0.3", "172.17.0.1", ["192.168.1.6"]);
        Assert.Equal(PathKind.ViaHub, Plan([hub, a, b]).Path(2, 3)!.Kind);
        Assert.Equal(PathKind.Lan, Plan([a, b]).Path(2, 3)!.Kind);
        // the same public exit address proves it
        var a2 = a with { PeerIp = "9.9.9.9" };
        var b2 = b with { PeerIp = "9.9.9.9" };
        Assert.Equal(PathKind.Lan, Plan([hub, a2, b2]).Path(2, 3)!.Kind);
    }

    [Fact]
    public void The_address_the_mesh_sees_proves_a_lan_when_the_server_sees_only_its_own_nat()
    {
        // the server behind Docker's NAT: every agent comes from 172.17.0.1, but the hub sees both homes' packets
        // arrive from the same router
        var hub = Node(1, "10.77.0.1", "172.17.0.1", ["1.1.1.1"]);
        var nas = Node(2, "10.77.0.2", "172.17.0.1", ["192.168.68.2"]) with { SeenFrom = "203.0.113.2" };
        var lab = Node(3, "10.77.0.3", "172.17.0.1", ["192.168.68.30"]) with { SeenFrom = "203.0.113.2" };
        var plan = Plan([hub, nas, lab]);
        Assert.Equal(PathKind.Lan, plan.Path(2, 3)!.Kind);
        Assert.Equal("192.168.68.30:51821", Peer(plan.Configs[2], "10.77.0.3").Endpoint);
        // another router: through the hub
        Assert.Equal(PathKind.ViaHub, Plan([hub, nas, lab with { SeenFrom = "203.0.113.9" }]).Path(2, 3)!.Kind);
    }

    /// <summary>All the nodes in one trusted network (id 1).</summary>
    private static MeshPlan PlanTrusted(MeshNode[] nodes) =>
        MeshPlanner.Plan(nodes, [new MeshNetworkDef(1, "home", null, nodes.Select(n => n.AgentId).ToHashSet(), Trusted: true)], Settings);

    [Fact]
    public void A_trusted_network_routes_one_lan_plainly_without_a_tunnel()
    {
        var nas = Node(2, "10.77.0.2", "9.9.9.9", ["192.168.68.2"]) with { PlainRoutes = true };
        var lab = Node(3, "10.77.0.3", "9.9.9.9", ["192.168.68.30"]) with { PlainRoutes = true };
        var plan = PlanTrusted([nas, lab]);
        Assert.Equal(PathKind.Plain, plan.Path(2, 3)!.Kind);
        Assert.Equal(PathKind.Plain, plan.Path(3, 2)!.Kind);
        var cfg = plan.Configs[2];
        Assert.Empty(cfg.Peers);
        Assert.Equal("10.77.0.3", Assert.Single(cfg.Routes).MeshIp);
        Assert.Equal("192.168.68.30", cfg.Routes[0].Via);
        Assert.Equal("192.168.68.2", Assert.Single(plan.Configs[3].Routes).Via);
        // the same hosts in a network that is not trusted: the tunnel over the LAN
        Assert.Equal(PathKind.Lan, Plan([nas, lab]).Path(2, 3)!.Kind);
    }

    [Fact]
    public void An_agent_without_plain_routes_keeps_its_pairs_in_the_tunnel()
    {
        var nas = Node(2, "10.77.0.2", "9.9.9.9", ["192.168.68.2"]) with { PlainRoutes = true };
        var old = Node(3, "10.77.0.3", "9.9.9.9", ["192.168.68.30"]);
        var plan = PlanTrusted([nas, old]);
        Assert.Equal(PathKind.Lan, plan.Path(2, 3)!.Kind);
        Assert.Empty(plan.Configs[2].Routes);
        Assert.Equal("192.168.68.30:51821", Peer(plan.Configs[2], "10.77.0.3").Endpoint);
    }

    [Fact]
    public void Public_hosts_on_a_shared_private_subnet_of_a_trusted_network_go_plain()
    {
        // two VPS with the provider's private network on eth1
        var a = Node(1, "10.77.0.1", "1.1.1.1", ["1.1.1.1", "10.20.0.5"]) with { PlainRoutes = true };
        var b = Node(2, "10.77.0.2", "2.2.2.2", ["2.2.2.2", "10.20.0.6"]) with { PlainRoutes = true };
        var plan = PlanTrusted([a, b]);
        Assert.Equal(PathKind.Plain, plan.Path(1, 2)!.Kind);
        Assert.Equal("10.20.0.6", Assert.Single(plan.Configs[1].Routes).Via);
        // without a shared private subnet the internet is in between: the tunnel stays
        var c = b with { HostIps = ["2.2.2.2"] };
        Assert.Equal(PathKind.Direct, PlanTrusted([a, c]).Path(1, 2)!.Kind);
    }

    [Fact]
    public void A_hub_that_relays_for_a_host_keeps_its_tunnel_to_it_on_a_trusted_lan()
    {
        // the hub is public (a forwarded port) and on the same LAN as nas; lab is behind another NAT and goes via the hub
        var hub = Node(1, "10.77.0.1", "9.9.9.9", ["192.168.68.10"], publicOverride: true, endpoint: "home.example.org:51821") with { PlainRoutes = true };
        var nas = Node(2, "10.77.0.2", "9.9.9.9", ["192.168.68.2"]) with { PlainRoutes = true };
        var far = Node(3, "10.77.0.3", "8.8.8.8", ["10.0.0.5"]) with { PlainRoutes = true };
        var plan = PlanTrusted([hub, nas, far]);
        Assert.Equal(PathKind.ViaHub, plan.Path(2, 3)!.Kind);
        Assert.Equal(PathKind.Lan, plan.Path(2, 1)!.Kind);
        Assert.Equal(PathKind.Lan, plan.Path(1, 2)!.Kind);
        var toHub = Peer(plan.Configs[2], "10.77.0.1");
        Assert.Equal("192.168.68.10:51821", toHub.Endpoint);
        Assert.Contains("10.77.0.3/32", toHub.AllowedIps);
    }

    [Fact]
    public void Plain_routes_are_part_of_the_config_hash()
    {
        var nas = Node(2, "10.77.0.2", "9.9.9.9", ["192.168.68.2"]) with { PlainRoutes = true };
        var lab = Node(3, "10.77.0.3", "9.9.9.9", ["192.168.68.30"]) with { PlainRoutes = true };
        var moved = PlanTrusted([nas, lab with { HostIps = ["192.168.68.31"] }]).Configs[2];
        Assert.NotEqual(PlanTrusted([nas, lab]).Configs[2].ConfigHash, moved.ConfigHash);
    }

    [Theory]
    [InlineData("203.0.113.2:51821", "203.0.113.2")]
    [InlineData("203.0.113.2", "203.0.113.2")]
    [InlineData("[2001:db8::1]:51821", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Endpoint_host_is_the_ipv4_address(string? endpoint, string? host) =>
        Assert.Equal(host, MeshPlanner.EndpointHost(endpoint));

    [Fact]
    public void The_server_host_behind_nat_is_not_public_by_itself()
    {
        // the usual home setup: Dockmaster on a NAS/LXC behind the router (TCP forwarded for agents), plus a VPS
        var srv = Node(1, "10.77.0.1", "172.17.0.1", ["192.168.68.42"], serverHost: true);
        var vps = Node(2, "10.77.0.2", "173.249.0.192", ["173.249.0.192"]);
        var plan = Plan([srv, vps]);
        Assert.Empty(plan.Problems);
        Assert.Equal([2], plan.PublicIds);
        Assert.Equal(2, plan.Hubs[1]);
        // the home host dials out to the VPS and keeps the NAT mapping open; no router change needed
        var toVps = Peer(plan.Configs[1], "10.77.0.2");
        Assert.Equal("173.249.0.192:51821", toVps.Endpoint);
        Assert.Equal(25, toVps.Keepalive);
        Assert.Equal("", Peer(plan.Configs[2], "10.77.0.1").Endpoint);
    }

    [Fact]
    public void The_server_host_marked_public_uses_the_public_url_host_and_is_the_hub()
    {
        var srv = Node(1, "10.77.0.1", "8.8.8.8", ["192.168.1.2"], publicOverride: true, serverHost: true);
        var pub = Node(3, "10.77.0.3", "3.3.3.3", ["3.3.3.3"]);
        var nat = Node(2, "10.77.0.2", "9.9.9.9", ["10.0.0.5"]);
        var plan = Plan([pub, srv, nat]);
        Assert.Empty(plan.Problems);
        Assert.Equal("dm.example.com:51821", Peer(plan.Configs[2], "10.77.0.1").Endpoint);
        Assert.Equal(1, plan.Hubs[1]);
        // forwarded 51900 outside to 51821 inside: the endpoint carries the outside port
        var forwarded = Plan([srv with { EndpointOverride = "dm.example.com:51900" }, nat]);
        Assert.Equal("dm.example.com:51900", Peer(forwarded.Configs[2], "10.77.0.1").Endpoint);
    }

    [Fact]
    public void Overrides_win()
    {
        var a = Node(1, "10.77.0.1", "8.8.8.8", ["192.168.1.2"], publicOverride: true, endpoint: "vpn.example.org:4000");
        var b = Node(2, "10.77.0.2", "2.2.2.2", ["2.2.2.2"], publicOverride: false);
        var plan = Plan([a, b]);
        Assert.Equal([1], plan.PublicIds);
        Assert.Equal("vpn.example.org:4000", Peer(plan.Configs[2], "10.77.0.1").Endpoint);
        Assert.Equal("", Peer(plan.Configs[1], "10.77.0.2").Endpoint);
        var noPort = Node(1, "10.77.0.1", null, [], publicOverride: true, endpoint: "vpn.example.org");
        Assert.Equal("vpn.example.org:51821", MeshPlanner.Endpoint(noPort, Settings));
        var publicNoAddress = Node(3, "10.77.0.3", null, [], publicOverride: true);
        Assert.Contains(Plan([publicNoAddress, b]).Problems, p => p.Contains("set its endpoint"));
    }

    [Fact]
    public void The_chosen_hub_is_used_when_it_is_public()
    {
        var a = Node(1, "10.77.0.1", "1.1.1.1", ["1.1.1.1"]);
        var b = Node(2, "10.77.0.2", "2.2.2.2", ["2.2.2.2"]);
        var c = Node(3, "10.77.0.3", "9.9.9.9", ["10.0.0.5"]);
        var d = Node(4, "10.77.0.4", "8.8.8.8", ["10.0.0.5"]);
        Assert.Equal(2, Plan([a, b, c, d], hub: 2).Hubs[1]);
        Assert.Equal(1, Plan([a, b, c, d], hub: 3).Hubs[1]); // c is not public: ignored
        var plan = Plan([a, b, c, d], hub: 2);
        Assert.Equal(["10.77.0.2/32", "10.77.0.4/32"], Peer(plan.Configs[3], "10.77.0.2").AllowedIps);
        Assert.Equal(["10.77.0.1/32"], Peer(plan.Configs[3], "10.77.0.1").AllowedIps);
    }

    [Fact]
    public void Hash_is_stable_across_peer_order_and_changes_with_content()
    {
        var a = Node(1, "10.77.0.1", "1.1.1.1", ["1.1.1.1"]);
        var b = Node(2, "10.77.0.2", "2.2.2.2", ["2.2.2.2"]);
        var c = Node(3, "10.77.0.3", "3.3.3.3", ["3.3.3.3"]);
        var h1 = Plan([a, b, c]).Configs[1].ConfigHash;
        var h2 = Plan([c, a, b]).Configs[1].ConfigHash;
        Assert.Equal(h1, h2);
        Assert.Equal(16, h1.Length);
        var h3 = Plan([a, b]).Configs[1].ConfigHash;
        Assert.NotEqual(h1, h3);
    }

    [Fact]
    public void Separate_networks_do_not_see_each_other()
    {
        // comp1 + comp2 in "home", comp3 + comp4 in "prod"
        var n1 = Node(1, "10.77.0.1", "9.9.9.9", ["192.168.1.1"]);
        var n2 = Node(2, "10.77.0.2", "9.9.9.9", ["192.168.1.2"]);
        var n3 = Node(3, "10.77.0.3", "3.3.3.3", ["3.3.3.3"]);
        var n4 = Node(4, "10.77.0.4", "8.8.8.8", ["10.0.0.4"]);
        var plan = MeshPlanner.Plan([n1, n2, n3, n4],
            [new MeshNetworkDef(1, "home", null, new HashSet<int> { 1, 2 }), new MeshNetworkDef(2, "prod", null, new HashSet<int> { 3, 4 })], Settings);
        Assert.Empty(plan.Problems);
        Assert.Equal(PathKind.Lan, plan.Path(1, 2)!.Kind);
        Assert.Equal(PathKind.Direct, plan.Path(3, 4)!.Kind);
        Assert.Null(plan.Path(1, 3));
        Assert.Null(plan.Path(2, 4));
        Assert.Equal(["10.77.0.2"], plan.Configs[1].Peers.Select(p => p.MeshIp));
        Assert.Equal(["10.77.0.4"], plan.Configs[3].Peers.Select(p => p.MeshIp));
        Assert.Null(plan.Hubs[1]);
        Assert.Equal(3, plan.Hubs[2]);
    }

    [Fact]
    public void A_hub_shared_by_two_networks_forwards_only_within_each()
    {
        // one VPS serves as the hub of "home" and of "office"; their members behind NAT must not reach each other
        var vps = Node(1, "10.77.0.1", null, ["1.1.1.1"]);
        var h1 = Node(2, "10.77.0.2", "8.8.8.8", ["192.168.1.2"]);
        var h2 = Node(3, "10.77.0.3", "7.7.7.7", ["10.1.0.3"]);
        var o1 = Node(4, "10.77.0.4", "6.6.6.6", ["10.2.0.4"]);
        var o2 = Node(5, "10.77.0.5", "5.5.5.5", ["10.3.0.5"]);
        var plan = MeshPlanner.Plan([vps, h1, h2, o1, o2],
            [new MeshNetworkDef(1, "home", null, new HashSet<int> { 1, 2, 3 }), new MeshNetworkDef(2, "office", null, new HashSet<int> { 1, 4, 5 })], Settings);
        Assert.Empty(plan.Problems);
        Assert.Equal(PathKind.ViaHub, plan.Path(2, 3)!.Kind);
        Assert.Equal(PathKind.ViaHub, plan.Path(4, 5)!.Kind);
        Assert.Null(plan.Path(2, 4));
        // home members route only home addresses through the hub
        Assert.Equal(["10.77.0.1/32", "10.77.0.3/32"], Peer(plan.Configs[2], "10.77.0.1").AllowedIps);
        Assert.Equal(["10.77.0.1/32", "10.77.0.5/32"], Peer(plan.Configs[4], "10.77.0.1").AllowedIps);
        // the hub gets one forwarding group per network
        var fwd = plan.Configs[1].Forward.ToDictionary(f => f.Network, f => f.Ips.ToArray());
        Assert.Equal(["10.77.0.2", "10.77.0.3"], fwd["home"]);
        Assert.Equal(["10.77.0.4", "10.77.0.5"], fwd["office"]);
        Assert.Empty(plan.Configs[2].Forward);
        Assert.True(plan.IsHub(1));
    }

    [Fact]
    public void Members_of_no_network_have_no_peers()
    {
        var a = Node(1, "10.77.0.1", null, ["1.1.1.1"]);
        var b = Node(2, "10.77.0.2", null, ["2.2.2.2"]);
        var plan = MeshPlanner.Plan([a, b], [], Settings);
        Assert.Empty(plan.Paths);
        Assert.Empty(plan.Configs[1].Peers);
        Assert.Empty(plan.Problems);
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("not-an-ip", false)]
    public void Knows_private_addresses(string ip, bool isPrivate) => Assert.Equal(isPrivate, MeshPlanner.IsPrivate(ip));

    [Fact]
    public void The_tunnel_mtu_follows_each_hosts_measured_paths()
    {
        var full = Node(1, "10.77.0.1", "1.1.1.1", ["1.1.1.1"]) with { PathMtu = 1500 };
        var pppoe = Node(2, "10.77.0.2", "2.2.2.2", ["2.2.2.2"]) with { PathMtu = 1492 };
        var unknown = Node(3, "10.77.0.3", "3.3.3.3", ["3.3.3.3"]);
        var tiny = Node(4, "10.77.0.4", "4.4.4.4", ["4.4.4.4"]) with { PathMtu = 900 };
        var plan = Plan([full, pppoe, unknown, tiny]);
        Assert.Equal(1440, plan.Configs[1].Mtu);   // 1500 - 60
        Assert.Equal(1432, plan.Configs[2].Mtu);   // 1492 - 60
        Assert.Equal(MeshPlanner.DefaultMtu, plan.Configs[3].Mtu);
        Assert.Equal(MeshPlanner.MinMtu, plan.Configs[4].Mtu);
        // a new MTU is a new config: the host gets it pushed
        Assert.NotEqual(plan.Configs[1].ConfigHash, MeshPlanner.Hash(new MeshApply(plan.Configs[1]) { Mtu = 1420 }));
    }

    [Fact]
    public void An_mtu_set_by_hand_wins_over_the_measured_one()
    {
        var a = Node(1, "10.77.0.1", "1.1.1.1", ["1.1.1.1"]) with { PathMtu = 1500 };
        var plan = MeshPlanner.Plan([a], [new MeshNetworkDef(1, "main", null, new HashSet<int> { 1 })], Settings with { Mtu = 1380 });
        Assert.Equal(1380, plan.Configs[1].Mtu);
    }

    [Fact]
    public void Tcp_tuning_goes_into_every_config_and_its_hash()
    {
        var a = Node(1, "10.77.0.1", "1.1.1.1", ["1.1.1.1"]);
        var plain = Plan([a]).Configs[1];
        var tuned = MeshPlanner.Plan([a], [new MeshNetworkDef(1, "main", null, new HashSet<int> { 1 })], Settings with { TcpTuning = true }).Configs[1];
        Assert.False(plain.TcpTuning);
        Assert.True(tuned.TcpTuning);
        Assert.NotEqual(plain.ConfigHash, tuned.ConfigHash);
    }

    [Fact]
    public void Hosts_that_go_through_a_hub_get_a_hint_how_to_go_direct()
    {
        var hub = Node(1, "10.77.0.1", "1.1.1.1", ["1.1.1.1"]);
        var home = Node(2, "10.77.0.2", "9.9.9.9", ["192.168.1.5"]);
        var office = Node(3, "10.77.0.3", "8.8.8.8", ["10.0.0.5"]);
        var plan = Plan([hub, home, office]);
        Assert.Equal(PathKind.ViaHub, plan.Path(2, 3)!.Kind);
        var hint = Assert.Single(plan.Hints);
        Assert.StartsWith("main: s2, s3 reach each other through s1", hint);
        Assert.Contains("UDP port 51821", hint);
        // direct paths only: nothing to suggest
        Assert.Empty(Plan([hub, home]).Hints);
    }
}
