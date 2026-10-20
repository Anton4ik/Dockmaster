using System.Net;

namespace Dockmaster.Tests;

/// <summary>A page of another origin (another port of the same host too) cannot change anything with the admin's cookie.</summary>
[Collection(ServerCollection.Name)]
public sealed class CsrfTests(ServerFixture f)
{
    private async Task<HttpStatusCode> KillAsync(params (string Header, string Value)[] headers)
    {
        var admin = await f.Client("admin");
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/agents/{f.Gamma}/containers/web/kill");
        foreach (var (h, v) in headers) req.Headers.TryAddWithoutValidation(h, v);
        return (await admin.SendAsync(req)).StatusCode;
    }

    [Fact]
    public async Task A_change_from_another_port_of_the_same_host_is_refused()
    {
        Assert.Equal(HttpStatusCode.Forbidden, await KillAsync(("Origin", "http://localhost:5000")));
        Assert.Equal(HttpStatusCode.Forbidden, await KillAsync(("Origin", "null")));
        Assert.Equal(HttpStatusCode.Forbidden, await KillAsync(("Sec-Fetch-Site", "same-site")));
        Assert.Equal(HttpStatusCode.Forbidden, await KillAsync(("Sec-Fetch-Site", "cross-site")));
    }

    [Fact]
    public async Task A_change_from_the_ui_itself_or_from_no_browser_goes_through()
    {
        // nobody is connected: a request that got as far as the server is answered 503
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await KillAsync(("Origin", "http://localhost"), ("Sec-Fetch-Site", "same-origin")));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await KillAsync());
    }

    [Fact]
    public async Task Reading_and_webhooks_are_not_guarded()
    {
        var admin = await f.Client("admin");
        var get = new HttpRequestMessage(HttpMethod.Get, "/api/agents");
        get.Headers.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(get)).StatusCode);
        var hook = new HttpRequestMessage(HttpMethod.Post, "/api/hooks/not-a-token");
        hook.Headers.Add("Origin", "https://ci.example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await f.Anonymous().SendAsync(hook)).StatusCode);
    }
}
