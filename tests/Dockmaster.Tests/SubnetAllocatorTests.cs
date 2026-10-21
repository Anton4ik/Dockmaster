using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

public class SubnetAllocatorTests
{
    [Theory]
    [InlineData("10.77.0.0/16", "", "10.77.0.1")]
    [InlineData("10.77.0.0/16", "10.77.0.1,10.77.0.2", "10.77.0.3")]
    [InlineData("10.77.0.0/16", "10.77.0.2", "10.77.0.1")]
    [InlineData("192.168.50.0/24", "192.168.50.1", "192.168.50.2")]
    [InlineData("10.77.0.0/16", "10.77.0.1,10.77.0.3,10.77.0.2,10.77.0.4,10.77.0.5,10.77.0.6,10.77.0.7,10.77.0.8,10.77.0.9,10.77.0.10", "10.77.0.11")]
    public void Allocates_the_lowest_free_address(string cidr, string used, string expected) =>
        Assert.Equal(expected, SubnetAllocator.Next(cidr, used.Split(',', StringSplitOptions.RemoveEmptyEntries)));

    [Fact]
    public void Allocation_skips_network_and_broadcast_and_fails_when_full()
    {
        // /30: .0 network, .1 and .2 hosts, .3 broadcast
        Assert.Equal("10.0.0.1", SubnetAllocator.Next("10.0.0.0/30", []));
        Assert.Equal("10.0.0.2", SubnetAllocator.Next("10.0.0.0/30", ["10.0.0.1"]));
        Assert.Throws<InvalidOperationException>(() => SubnetAllocator.Next("10.0.0.0/30", ["10.0.0.1", "10.0.0.2"]));
        Assert.True(SubnetAllocator.Contains("10.77.0.0/16", "10.77.200.4"));
        Assert.False(SubnetAllocator.Contains("10.77.0.0/16", "10.78.0.4"));
    }

    [Theory]
    [InlineData("10.77.0.0/16", null)]
    [InlineData("192.168.50.0/24", null)]
    [InlineData("10.77.0.0", "subnet must look like 10.77.0.0/16")]
    [InlineData("8.8.0.0/16", "subnet must be a private range (10.x, 172.16-31.x, 192.168.x)")]
    [InlineData("10.77.0.0/28", "subnet prefix must be between /8 and /24")]
    [InlineData("10.77.1.0/16", "subnet address has host bits set — did you mean 10.77.0.0/16?")]
    public void Validates_subnets(string cidr, string? error) => Assert.Equal(error, SubnetAllocator.Validate(cidr));
}
