using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Dockmaster.Server.Access;
using Dockmaster.Server.Data;

namespace Dockmaster.Tests;

/// <summary>
/// The whole server in memory (its own data folder, no agents connected) with three servers, a project on each of the
/// first two, and users with different access:
/// <list type="bullet">
/// <item>admin — the admin (DOCKMASTER_ADMINPASSWORD)</item>
/// <item>viewer — Viewer on alpha</item>
/// <item>operator — Operator on alpha</item>
/// <item>dev — Developer on alpha, Viewer on beta</item>
/// <item>global — Operator on every server</item>
/// <item>nobody — no grants</item>
/// <item>gone — disabled</item>
/// </list>
/// Commands to the servers fail with 503 (nobody is connected): a request that gets that far was allowed.
/// </summary>
public sealed class ServerFixture : IAsyncLifetime
{
    public const string Password = "password-123";
    public const string AdminPassword = "admin-password-1";
    private static readonly SemaphoreSlim Starting = new(1, 1);

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "dockmaster-tests-" + Guid.NewGuid().ToString("N"));
    public WebApplicationFactory<AccessService> App { get; private set; } = null!;
    public int Alpha { get; private set; }
    public int Beta { get; private set; }
    public int Gamma { get; private set; }
    public int AlphaProject { get; private set; }
    public int BetaProject { get; private set; }
    public Dictionary<string, int> UserIds { get; } = [];
    public Dictionary<string, int> RoleIds { get; private set; } = [];

    public async Task InitializeAsync()
    {
        App = await StartAsync(_dataDir, AdminPassword);

        await using var db = await Db();
        RoleIds = await db.Roles.ToDictionaryAsync(r => r.Name, r => r.Id);
        var alpha = new AgentEntity { Name = "alpha", TokenHash = "a" };
        var beta = new AgentEntity { Name = "beta", TokenHash = "b" };
        var gamma = new AgentEntity { Name = "gamma", TokenHash = "c" };
        db.Agents.AddRange(alpha, beta, gamma);
        await db.SaveChangesAsync();
        (Alpha, Beta, Gamma) = (alpha.Id, beta.Id, gamma.Id);
        var shop = new ProjectEntity { Name = "shop", AgentId = Alpha, ComposeYaml = "services:\n  web:\n    image: nginx\n", EnvFile = "DB_PASSWORD=hunter2\n" };
        var blog = new ProjectEntity { Name = "blog", AgentId = Beta, ComposeYaml = "services:\n  web:\n    image: nginx\n", EnvFile = "TOKEN=secret\n" };
        db.Projects.AddRange(shop, blog);
        await db.SaveChangesAsync();
        (AlphaProject, BetaProject) = (shop.Id, blog.Id);

        await AddUserAsync(db, "viewer", (Alpha, "Viewer"));
        await AddUserAsync(db, "operator", (Alpha, "Operator"));
        await AddUserAsync(db, "dev", (Alpha, "Developer"), (Beta, "Viewer"));
        await AddUserAsync(db, "global", (UserGrantEntity.AllServers, "Operator"));
        await AddUserAsync(db, "nobody");
        await AddUserAsync(db, "gone");
        (await db.Users.SingleAsync(u => u.Name == "gone")).Disabled = true;
        await db.SaveChangesAsync();
        UserIds["admin"] = (await db.Users.SingleAsync(u => u.Name == "admin")).Id;
        Access.Changed([]);
    }

    /// <summary>A server in memory on its own data folder; null = no admin password (the first run).</summary>
    public static async Task<WebApplicationFactory<AccessService>> StartAsync(string dataDir, string? adminPassword)
    {
        Directory.CreateDirectory(dataDir);
        // Program reads these before the host is built, where the factory's own settings are not there yet
        await Starting.WaitAsync();
        try
        {
            Environment.SetEnvironmentVariable("DOCKMASTER_DATADIR", dataDir);
            Environment.SetEnvironmentVariable("DOCKMASTER_ADMINPASSWORD", adminPassword);
            var app = new WebApplicationFactory<AccessService>().WithWebHostBuilder(b => b.UseEnvironment("Testing"));
            _ = app.Server; // starts it
            return app;
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOCKMASTER_DATADIR", null);
            Environment.SetEnvironmentVariable("DOCKMASTER_ADMINPASSWORD", null);
            Starting.Release();
        }
    }

    private async Task AddUserAsync(DockmasterDb db, string name, params (int Agent, string Role)[] grants)
    {
        var u = new UserEntity { Name = name, PasswordHash = Passwords.Hash(Password), SessionStamp = Passwords.NewStamp() };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        db.UserGrants.AddRange(grants.Select(g => new UserGrantEntity { UserId = u.Id, AgentId = g.Agent, RoleId = RoleIds[g.Role] }));
        await db.SaveChangesAsync();
        UserIds[name] = u.Id;
    }

    public AccessService Access => App.Services.GetRequiredService<AccessService>();

    public Task<DockmasterDb> Db() => App.Services.GetRequiredService<IDbContextFactory<DockmasterDb>>().CreateDbContextAsync();

    /// <summary>A client that keeps its cookie, not signed in.</summary>
    public HttpClient Anonymous() => App.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });

    private readonly Dictionary<string, HttpClient> _signedIn = [];

    /// <summary>A client signed in as one of the fixture's users, kept for the next test (signing in takes a while: PBKDF2).</summary>
    public async Task<HttpClient> Client(string user)
    {
        if (!_signedIn.TryGetValue(user, out var c)) _signedIn[user] = c = await As(user);
        return c;
    }

    /// <summary>A client signed in as the user.</summary>
    public async Task<HttpClient> As(string user)
    {
        var c = Anonymous();
        var r = await c.PostAsJsonAsync("/api/auth/login", new { name = user, password = user == "admin" ? AdminPassword : Password });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return c;
    }

    /// <summary>A hub connection signed in as the user (long polling through the test server, the cookie in a header).</summary>
    public async Task<HubConnection> Hub(string user)
    {
        var handler = App.Server.CreateHandler();
        var c = new HttpClient(handler) { BaseAddress = App.Server.BaseAddress };
        var r = await c.PostAsJsonAsync("/api/auth/login", new { name = user, password = user == "admin" ? AdminPassword : Password });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var cookie = string.Join("; ", r.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0]));
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(App.Server.BaseAddress, "/hub"), o =>
            {
                o.HttpMessageHandlerFactory = _ => handler;
                o.Transports = HttpTransportType.LongPolling;
                o.Headers["Cookie"] = cookie;
            })
            .Build();
        await hub.StartAsync();
        return hub;
    }

    public async Task DisposeAsync()
    {
        await App.DisposeAsync();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

[CollectionDefinition(Name)]
public sealed class ServerCollection : ICollectionFixture<ServerFixture>
{
    public const string Name = "server";
}
