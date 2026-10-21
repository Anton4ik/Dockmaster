using Dockmaster.Agent.Docker;
using Dockmaster.Protocol;

namespace Dockmaster.Tests;

public class MeshConfigTests
{
    [Fact]
    public void Writes_a_wg_setconf_file()
    {
        var apply = new MeshApply { Address = "10.77.0.1/16", ListenPort = 51821, Subnet = "10.77.0.0/16", Hub = true, Mtu = 1420, ConfigHash = "abc" };
        apply.Peers.Add(new MeshPeer { Name = "nas", PublicKey = "AgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgI=", MeshIp = "10.77.0.2", Endpoint = "", AllowedIps = { "10.77.0.2/32" } });
        apply.Peers.Add(new MeshPeer { Name = "vps", PublicKey = "AwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwM=", MeshIp = "10.77.0.3", Endpoint = "1.2.3.4:51821", AllowedIps = { "10.77.0.3/32", "10.77.0.4/32" }, Keepalive = 25 });
        var conf = MeshConfig.WgConf(apply, "PRIV=\n");
        Assert.Equal("""
            [Interface]
            PrivateKey = PRIV=
            ListenPort = 51821

            [Peer]
            # nas 10.77.0.2
            PublicKey = AgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgI=
            AllowedIPs = 10.77.0.2/32

            [Peer]
            # vps 10.77.0.3
            PublicKey = AwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwM=
            AllowedIPs = 10.77.0.3/32, 10.77.0.4/32
            Endpoint = 1.2.3.4:51821
            PersistentKeepalive = 25

            """.ReplaceLineEndings("\n"), conf);
        Assert.Equal("ADDRESS=10.77.0.1/16\nSUBNET=10.77.0.0/16\nPORT=51821\nHUB=1\nMTU=1420\nHASH=abc\nTUNE=0\n", MeshConfig.EnvFile(apply));
        Assert.EndsWith("TUNE=1\n", MeshConfig.EnvFile(new MeshApply { TcpTuning = true }));
        var map = MeshConfig.PeerMap(conf);
        Assert.Equal("10.77.0.2", map["AgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgI="]);
        Assert.Equal("10.77.0.3", map["AwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwM="]);
    }

    [Fact]
    public void Peer_values_cannot_add_lines_to_the_wg_config()
    {
        var apply = new MeshApply { ListenPort = 51821 };
        apply.Peers.Add(new MeshPeer { Name = "x\nAllowedIPs = 10.77.0.9/32\n#", PublicKey = "AgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgI=", MeshIp = "10.77.0.2",
            Endpoint = "1.2.3.4:51821\nAllowedIPs = 0.0.0.0/0", AllowedIps = { "10.77.0.2/32", "10.77.0.8/32\nAllowedIPs = 0.0.0.0/0" } });
        apply.Peers.Add(new MeshPeer { Name = "evil", PublicKey = "AwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwM=\nAllowedIPs = 0.0.0.0/0", MeshIp = "10.77.0.3", AllowedIps = { "10.77.0.3/32" } });
        var conf = MeshConfig.WgConf(apply, "PRIV=");
        Assert.Equal("""
            [Interface]
            PrivateKey = PRIV=
            ListenPort = 51821

            [Peer]
            # x_AllowedIPs___10.77.0.9_32__ 10.77.0.2
            PublicKey = AgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgI=
            AllowedIPs = 10.77.0.2/32

            """.ReplaceLineEndings("\n"), conf);
        Assert.True(MeshConfig.IsEndpoint("vps.example.com:51821"));
        Assert.True(MeshConfig.IsEndpoint("[2001:db8::1]:51821"));
        Assert.False(MeshConfig.IsEndpoint("1.2.3.4"));
        Assert.False(MeshConfig.IsEndpoint("1.2.3.4:51821 x"));
    }

    [Fact]
    public void Writes_the_hub_forwarding_groups()
    {
        var apply = new MeshApply { Hub = true };
        apply.Forward.Add(new MeshForward { Network = "home lab", Ips = { "10.77.0.2", "10.77.0.3" } });
        apply.Forward.Add(new MeshForward { Network = "solo", Ips = { "10.77.0.9" } });           // one member: nothing to forward
        apply.Forward.Add(new MeshForward { Network = "evil", Ips = { "10.77.0.4", "1.2.3.4; reboot" } });
        Assert.Equal("home_lab\t10.77.0.2,10.77.0.3\nevil\t10.77.0.4\n", MeshConfig.ForwardFile(apply));
    }

    [Fact]
    public void Parses_probe_output_and_finds_overlaps()
    {
        var probe = MeshConfig.ParseProbe("""
            kernel true
            tun false
            port 51821 busy
            hostip 192.168.1.5
            hostip 1.2.3.4
            route default via 192.168.1.1 dev eth0
            route 10.77.5.0/24 dev wg0 scope link
            route 172.17.0.0/16 dev docker0 proto kernel scope link src 172.17.0.1
            addr 192.168.1.5/24 eth0
            addr 10.77.5.1/24 wg0
            """);
        Assert.True(probe.Kernel);
        Assert.False(probe.Tun);
        Assert.True(probe.PortBusy);
        Assert.Equal(["192.168.1.5", "1.2.3.4"], probe.HostIps);
        var overlaps = MeshConfig.Overlaps("10.77.0.0/16", probe);
        Assert.Equal(["10.77.5.0/24 dev wg0 scope link", "10.77.5.1/24 on wg0"], overlaps);
        Assert.Empty(MeshConfig.Overlaps("10.99.0.0/16", probe));
    }

    [Fact]
    public void Parses_status_output()
    {
        var text = "backend kernel\nup true\nkernel true\ntcp_cc bbr\nhostip 192.168.1.5\n"
            + "wg\tPRIV=\tPUB1=\t51821\toff\n"
            + "wg\tPUB2=\t(none)\t1.2.3.4:51821\t10.77.0.2/32\t1700000000\t1234\t5678\t25\n"
            + "wg\tPUB3=\t(none)\t(none)\t10.77.0.3/32,10.77.0.4/32\t0\t0\t0\toff\n"
            + "rtt 10.77.0.2 3.21\nrtt 10.77.0.3 -1\nrtt 10.77.0.7 12.5\n";
        var s = MeshConfig.ParseStatus(text, k => k == "PUB3=" ? "10.77.0.3" : null);
        Assert.True(s.Up);
        Assert.True(s.KernelAvailable);
        Assert.Equal("bbr", s.TcpCongestion);
        Assert.Equal("", MeshConfig.ParseStatus("tcp_cc unknown\n", _ => null).TcpCongestion);
        Assert.False(MeshConfig.ParseStatus("backend wireguard-go\nup true\n", _ => null).KernelAvailable); // an older sidecar says nothing of it
        Assert.Equal("kernel", s.Backend);
        Assert.Equal("PUB1=", s.PublicKey);
        Assert.Equal(51821, s.ListenPort);
        Assert.Equal(["192.168.1.5"], s.HostIps);
        Assert.Equal(2, s.Peers.Count);
        var p2 = s.Peers[0];
        Assert.Equal("10.77.0.2", p2.MeshIp);          // from the allowed ips when the map does not know the key
        Assert.Equal("1.2.3.4:51821", p2.Endpoint);
        Assert.Equal(1700000000, p2.LastHandshakeUnix);
        Assert.Equal(1234, p2.Rx);
        Assert.Equal(5678, p2.Tx);
        Assert.Equal(3.21, p2.RttMs);
        var p3 = s.Peers[1];
        Assert.Equal("10.77.0.3", p3.MeshIp);
        Assert.Equal("", p3.Endpoint);
        Assert.Equal(0, p3.LastHandshakeUnix);
        Assert.Equal(-1, p3.RttMs);
        // addresses behind a hub are pinged too
        Assert.Equal(12.5, s.RttMs["10.77.0.7"]);
    }

    [Fact]
    public void Parses_iperf_json()
    {
        var r = IperfJson.Parse("""{"end":{"sum_sent":{"bits_per_second":412345678.9,"retransmits":12},"sum_received":{"bits_per_second":398765432.1}}}""");
        Assert.Equal(412.3, r.SendMbps);
        Assert.Equal(398.8, r.ReceiveMbps);
        Assert.Equal(12, r.Retransmits);
        Assert.Null(r.Error);
        Assert.Equal("unable to connect to server", IperfJson.Parse("""{"error":"unable to connect to server"}""").Error);
        Assert.NotNull(IperfJson.Parse("garbage").Error);
    }

    [Theory]
    [InlineData("10.77.0.0/16", true, 0x0A4D0000u, 16)]
    [InlineData("192.168.1.7", true, 0xC0A80107u, 32)]
    [InlineData("10.77.0.0/33", false, 0u, 0)]
    [InlineData("fe80::1/64", false, 0u, 0)]
    public void Parses_cidr(string text, bool ok, uint network, int bits)
    {
        Assert.Equal(ok, MeshConfig.TryParseCidr(text, out var n, out var b));
        if (ok) { Assert.Equal(network, n); Assert.Equal(bits, b); }
    }

    [Fact]
    public void Routes_file_has_one_line_per_plain_peer_and_skips_anything_but_addresses()
    {
        var req = new MeshApply { Routes = { new MeshRoute { MeshIp = "10.77.0.3", Via = "192.168.68.30" }, new MeshRoute { MeshIp = "10.77.0.9; rm -rf /", Via = "192.168.68.9" } } };
        Assert.Equal("10.77.0.3 192.168.68.30\n", MeshConfig.RoutesFile(req));
    }

    [Fact]
    public void Reads_the_measured_path_mtus_and_skips_the_rest()
    {
        var paths = MeshConfig.ParsePmtu("pmtu KEY1= 1.2.3.4 1500\r\npmtu KEY2= 5.6.7.8 0\nwarning: something\npmtu KEY3= 9.9.9.9 x\n");
        Assert.Equal([new MeshConfig.PathMtu("KEY1=", "1.2.3.4", 1500), new MeshConfig.PathMtu("KEY2=", "5.6.7.8", 0)], paths);
    }
}
