using System.Net;
using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

public class RegistryHttpTests
{
    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("140.82.112.3", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.68.42", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.100.1.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("2606:4700::1111", true)]
    public void Tells_public_addresses(string address, bool expected) =>
        Assert.Equal(expected, RegistryHttp.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("https://ghcr.io/v2/", "https://ghcr.io/token", true)]
    [InlineData("https://ghcr.io/v2/", "http://ghcr.io/token", false)]
    [InlineData("https://ghcr.io/v2/", "file:///etc/passwd", false)]
    [InlineData("https://ghcr.io/v2/", "/token", false)]
    [InlineData("http://registry.lan:5000/v2/", "http://registry.lan:5000/token", true)]
    public void A_token_service_keeps_https(string registry, string realm, bool allowed) =>
        Assert.Equal(allowed, RegistryClient.RealmUri(registry, realm) != null);

    [Theory]
    [InlineData("/v2/_catalog?last=x&n=1000", "https://reg.example/v2/_catalog?last=x&n=1000")]
    [InlineData("https://reg.example/v2/_catalog?last=y", "https://reg.example/v2/_catalog?last=y")]
    [InlineData("https://169.254.169.254/latest/meta-data", null)]
    [InlineData("http://reg.example/v2/_catalog?last=y", null)]
    [InlineData("https://reg.example:8443/v2/_catalog", null)]
    public void Pages_stay_on_the_registry(string link, string? next) =>
        Assert.Equal(next, RegistryClient.NextPage("https://reg.example/v2/_catalog?n=1000", link));

    [Fact]
    public async Task A_public_only_request_does_not_connect_inside()
    {
        using var client = new HttpClient(RegistryHttp.Handler());
        var req = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:9/token");
        req.Options.Set(RegistryHttp.PublicOnly, true);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(req));
        Assert.Contains("not on a public address", ex.ToString());
    }
}
