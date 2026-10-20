using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Dockmaster.Server.Access;

namespace Dockmaster.Tests;

/// <summary>
/// Every kind of endpoint, called by every kind of user, on a server they have access to and one they do not: allowed
/// means the handler ran (whatever it answered, 503 for a server nobody connected), denied means 401, 403 or 404.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class AccessMatrixTests(ServerFixture f)
{
    private const string Admin = "admin", Anyone = "anyone";

    private static readonly string[] Viewer = [Perm.View];
    private static readonly string[] Operator = [Perm.View, Perm.Control, Perm.Deploy];
    private static readonly string[] Developer = [Perm.View, Perm.Control, Perm.Deploy, Perm.Edit, Perm.Secrets, Perm.Terminal, Perm.Volumes];

    /// <summary>What each user of the fixture may do where, written out independently of the code that decides it.</summary>
    private static readonly Dictionary<string, Dictionary<string, string[]>> Expected = new()
    {
        ["viewer"] = new() { ["alpha"] = Viewer },
        ["operator"] = new() { ["alpha"] = Operator },
        ["dev"] = new() { ["alpha"] = Developer, ["beta"] = Viewer },
        ["global"] = new() { ["alpha"] = Operator, ["beta"] = Operator, ["gamma"] = Operator },
        ["nobody"] = new(),
    };

    /// <summary>method, path ({server}, {project}: alpha's or beta's), body, what it takes.</summary>
    private static readonly (string Method, string Path, object? Body, string Needs)[] ServerEndpoints =
    [
        ("GET", "/api/agents/{server}", null, Perm.View),
        ("PUT", "/api/agents/{server}", new { name = "{serverName}" }, Perm.Server),
        ("POST", "/api/agents/{server}/update", null, Perm.Server),
        ("GET", "/api/agents/{server}/images", null, Perm.View),
        ("POST", "/api/agents/{server}/images/prune", null, Perm.Server),
        ("POST", "/api/agents/{server}/images/remove", new { ids = new[] { "sha256:x" } }, Perm.Server),
        ("GET", "/api/agents/{server}/volumes", null, Perm.View),
        ("POST", "/api/agents/{server}/volumes", new { name = "data" }, Perm.Volumes),
        ("POST", "/api/agents/{server}/volumes/remove", new { names = new[] { "data" } }, Perm.Volumes),
        ("GET", "/api/agents/{server}/volumes/data/browse", null, Perm.Volumes),
        ("GET", "/api/agents/{server}/networks", null, Perm.View),
        ("POST", "/api/agents/{server}/networks", new { name = "shared" }, Perm.Server),
        ("POST", "/api/agents/{server}/networks/remove", new { names = new[] { "shared" } }, Perm.Server),
        ("GET", "/api/agents/{server}/containers/web-1/inspect", null, Perm.View),
        ("POST", "/api/agents/{server}/containers/web-1/stop", null, Perm.Control),
        ("POST", "/api/agents/{server}/containers/web-1/remove", null, Perm.Control),
        ("POST", "/api/agents/{server}/containers/web-1/restart-policy", new { policy = "always" }, Perm.Edit),
        ("POST", "/api/agents/{server}/containers/web-1/networks", new { network = "shared", connect = true }, Perm.Edit),
        ("POST", "/api/agents/{server}/import", new { project = "legacy" }, Perm.Edit),
        ("GET", "/api/agents/{server}/metrics", null, Perm.View),
        ("GET", "/api/agents/{server}/containers/web-1/metrics", null, Perm.View),
        ("GET", "/api/projects/{project}", null, Perm.View),
        ("GET", "/api/projects/{project}/plan", null, Perm.View),
        ("GET", "/api/projects/{project}/deployments", null, Perm.View),
        ("GET", "/api/projects/{project}/files", null, Perm.View),
        ("GET", "/api/projects/{project}/secrets", null, Perm.View),
        ("GET", "/api/projects/{project}/jobs", null, Perm.View),
        ("GET", "/api/projects/{project}/metrics", null, Perm.View),
        ("GET", "/api/projects/{project}/autoscale", null, Perm.View),
        ("GET", "/api/projects/{project}/volumes", null, Perm.View),
        ("PUT", "/api/projects/{project}/files/app.conf", new { content = "x" }, Perm.Edit),
        ("PUT", "/api/projects/{project}/services/web/expose", new { ports = new[] { "80" } }, Perm.Edit),
        ("PUT", "/api/projects/{project}/services/web/limits", new { replicas = 1 }, Perm.Edit),
        ("POST", "/api/projects/{project}/services/web/run", null, Perm.Control),
        ("PUT", "/api/projects/{project}/secrets/api_key", new { value = "v" }, Perm.Secrets),
        ("GET", "/api/projects/{project}/hook", null, Perm.Secrets),
    ];

    private static readonly (string Method, string Path, object? Body, string Needs)[] GlobalEndpoints =
    [
        ("GET", "/api/agents", null, Anyone),
        ("GET", "/api/projects", null, Anyone),
        ("GET", "/api/events", null, Anyone),
        ("GET", "/api/mesh", null, Anyone),
        ("GET", "/api/version", null, Anyone),
        ("GET", "/api/projects/schedule-preview?cron=0+3+*+*+*", null, Anyone),
        ("POST", "/api/agents", new { name = "new", publicUrl = "https://dm.example:9443" }, Admin),
        ("GET", "/api/settings", null, Admin),
        ("GET", "/api/settings/notifications", null, Admin),
        ("GET", "/api/registries", null, Admin),
        ("GET", "/api/users", null, Admin),
        ("GET", "/api/roles", null, Admin),
        ("GET", "/api/roles/permissions", null, Admin),
        ("GET", "/api/templates", null, Admin),
        ("GET", "/api/apikeys", null, Admin),
        ("GET", "/api/system/backups", null, Admin),
        ("GET", "/api/mesh/connections", null, Admin),
        ("POST", "/api/mesh/networks", new { name = "" }, Admin),
    ];

    public static IEnumerable<object[]> ServerCases() =>
        from user in Expected.Keys
        from server in new[] { "alpha", "beta" }
        from i in Enumerable.Range(0, ServerEndpoints.Length)
        select new object[] { user, server, i };

    public static IEnumerable<object[]> GlobalCases() =>
        from user in Expected.Keys.Append("admin")
        from i in Enumerable.Range(0, GlobalEndpoints.Length)
        select new object[] { user, i };

    [Theory]
    [MemberData(nameof(ServerCases))]
    public async Task Server_endpoints_follow_the_users_permissions_on_that_server(string user, string server, int i)
    {
        var (method, path, body, needs) = ServerEndpoints[i];
        var allowed = Expected[user].TryGetValue(server, out var perms) && perms.Contains(needs);
        await AssertAsync(user, method, Fill(path, server), body, allowed);
    }

    [Theory]
    [InlineData("alpha")]
    [InlineData("beta")]
    public async Task The_admin_may_call_every_server_endpoint(string server)
    {
        foreach (var (method, path, body, _) in ServerEndpoints)
            await AssertAsync("admin", method, Fill(path, server), body, true);
    }

    [Theory]
    [MemberData(nameof(GlobalCases))]
    public async Task Global_endpoints_are_for_anyone_signed_in_or_for_admins(string user, int i)
    {
        var (method, path, body, needs) = GlobalEndpoints[i];
        await AssertAsync(user, method, path, body, needs == Anyone || user == "admin");
    }

    [Fact]
    public async Task Nothing_but_signing_in_and_webhooks_answers_without_a_session()
    {
        var anonymous = f.Anonymous();
        foreach (var (method, path, body, _) in ServerEndpoints.Concat(GlobalEndpoints))
        {
            var r = await SendAsync(anonymous, method, Fill(path, "alpha"), body);
            Assert.True(r.StatusCode == HttpStatusCode.Unauthorized, $"{method} {path} without a session: {(int)r.StatusCode}");
        }
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PostAsync("/api/hooks/1-nothing", null)).StatusCode);
    }

    /// <summary>A new endpoint without a rule would be for admins only — but it must say so: every /api endpoint names its rule.</summary>
    [Fact]
    public void Every_api_endpoint_has_an_access_rule()
    {
        var endpoints = f.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api") == true).ToList();
        Assert.True(endpoints.Count > 100, $"only {endpoints.Count} endpoints found");
        var missing = endpoints.Where(e => e.Metadata.GetMetadata<AccessRule>() == null).Select(e => e.DisplayName).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void An_endpoints_own_rule_wins_over_its_groups()
    {
        var endpoints = f.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        AccessRule RuleOf(string pattern, string method) => endpoints.Single(e => e.RoutePattern.RawText == pattern
            && e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods.Contains(method) == true).Metadata.GetMetadata<AccessRule>()!;
        Assert.Equal(AccessKind.SignedIn, RuleOf("/api/mesh/", "GET").Kind);
        Assert.Equal(AccessKind.Admin, RuleOf("/api/mesh/apply", "POST").Kind);
    }

    private string Fill(string path, string server) => path
        .Replace("{server}", (server == "alpha" ? f.Alpha : f.Beta).ToString())
        .Replace("{project}", (server == "alpha" ? f.AlphaProject : f.BetaProject).ToString());

    private async Task AssertAsync(string user, string method, string path, object? body, bool allowed)
    {
        var client = await f.Client(user);
        var r = await SendAsync(client, method, path, body is null ? null : Json(body, path));
        var denied = r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound;
        Assert.True(allowed != denied, $"{user} {method} {path}: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()} — expected {(allowed ? "allowed" : "denied")}");
    }

    // the server's own name where a body names it (a rename to the same name changes nothing)
    private object Json(object body, string path) =>
        body.GetType().GetProperty("name")?.GetValue(body) is "{serverName}" ? new { name = path.Contains($"/{f.Alpha}") ? "alpha" : "beta" } : body;

    private static Task<HttpResponseMessage> SendAsync(HttpClient c, string method, string path, object? body) =>
        c.SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = body is null ? null : JsonContent.Create(body) });
}
