using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Server.Access;
using Dockmaster.Server.Api;
using Dockmaster.Server.Data;
using Dockmaster.Server.Hubs;
using Dockmaster.Server.Services;
// Kestrel.Core has a type of the same name (for HttpProtocols above)
using BadHttpRequestException = Microsoft.AspNetCore.Http.BadHttpRequestException;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables("DOCKMASTER_");

var dataDir = builder.Configuration["DataDir"] ?? Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDir);
var uiPort = int.TryParse(builder.Configuration["UiPort"], out var up) ? up : 8080;
var agentPort = int.TryParse(builder.Configuration["AgentPort"], out var ap) ? ap : SettingsService.DefaultAgentPort;

// a backup uploaded for restore is put in place before anything opens the data
BackupService.ApplyPendingRestore(dataDir, Console.WriteLine);
var certs = new CertificateService(dataDir);
builder.Services.AddSingleton(certs);

builder.WebHost.ConfigureKestrel(k =>
{
    // HTTP/2 only runs on the agent port in practice: a connection whose agent went away without a word (a host that
    // lost power, a NAT that forgot it) is noticed within a minute instead of when TCP gives up
    k.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(30);
    k.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(20);
    // UI + REST API + SignalR (put a reverse proxy with TLS in front, or use it on the LAN)
    k.ListenAnyIP(uiPort, o => o.Protocols = HttpProtocols.Http1AndHttp2);
    // Agents: TLS + HTTP/2 with the self-signed certificate agents pin by fingerprint
    k.ListenAnyIP(agentPort, o =>
    {
        o.Protocols = HttpProtocols.Http2;
        o.UseHttps(certs.Certificate);
    });
});

builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys"))).SetApplicationName("dockmaster");
builder.Services.AddDbContextFactory<DockmasterDb>(o => o.UseSqlite($"Data Source={Path.Combine(dataDir, "dockmaster.db")}"));
builder.Services.AddHttpClient("registry", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.MaxResponseContentBufferSize = 16 << 20;
}).ConfigurePrimaryHttpMessageHandler(RegistryHttp.Handler);
builder.Services.AddHttpClient("seq", c => c.Timeout = TimeSpan.FromSeconds(15));
// finish before docker's default 10 s stop grace runs out: a killed container (exit 137) looks like a crash
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(8));
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<Watchers>();
builder.Services.AddSingleton<Pacer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Pacer>());
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton<RegistryClient>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<AccessService>();
builder.Services.AddSingleton<RunLogs>();
builder.Services.AddSingleton<ProjectService>();
builder.Services.AddSingleton<TemplateService>();
builder.Services.AddHostedService<AgentBroadcaster>();
builder.Services.AddSingleton<ImageUpdateChecker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ImageUpdateChecker>());
builder.Services.AddSingleton<MeshService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MeshService>());
builder.Services.AddSingleton<Notifier>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Notifier>());
builder.Services.AddSingleton<MetricsHistory>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MetricsHistory>());
builder.Services.AddHostedService<HealthWatch>();
builder.Services.AddSingleton<Autoscaler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Autoscaler>());
builder.Services.AddSingleton<JobIndex>();
builder.Services.AddSingleton<JobScheduler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobScheduler>());
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackupService>());

builder.Services.AddGrpc(o => o.MaxReceiveMessageSize = AgentHubService.MaxMessageSize);
builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 1024 * 1024);
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "dockmaster.auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.ExpireTimeSpan = TimeSpan.FromDays(30);
    o.SlidingExpiration = true;
    // SPA: never redirect, answer with status codes
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    // the user still exists, is not disabled and has not changed the password since: checked on every request
    o.Events.OnValidatePrincipal = async ctx =>
    {
        if (ctx.Principal != null && await ctx.HttpContext.RequestServices.GetRequiredService<AccessService>().ForAsync(ctx.Principal) != null) return;
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    };
});
builder.Services.AddAuthorization();
builder.Services.AddSingleton<SetupCode>();
builder.Services.AddSingleton<LoginThrottle>();
// sign-in and first-run requests per address; an in-process caller has none
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = (r, ct) => new ValueTask(r.HttpContext.Response.WriteAsJsonAsync(new { error = "too many attempts — try again in a minute" }, ct));
    o.AddPolicy(AuthApi.RateLimit, ctx => ctx.Connection.RemoteIpAddress is { } ip
        ? RateLimitPartition.GetFixedWindowLimiter(ip.ToString(), _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
        : RateLimitPartition.GetNoLimiter(""));
});

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await using (var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<DockmasterDb>>().CreateDbContextAsync())
    {
        await db.Database.EnsureCreatedAsync();
        await db.UpgradeSchemaAsync();
    }
    // the first admin without the setup page; once anyone can sign in, it is not looked at again
    var seed = builder.Configuration["AdminPassword"];
    await using (var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<DockmasterDb>>().CreateDbContextAsync())
        if (!string.IsNullOrWhiteSpace(seed) && !await db.Users.AnyAsync())
        {
            db.Users.Add(new UserEntity { Name = AuthApi.DefaultAdmin, PasswordHash = Passwords.Hash(seed), IsAdmin = true, SessionStamp = Passwords.NewStamp() });
            await db.SaveChangesAsync();
            app.Logger.LogInformation("admin user initialised from DOCKMASTER_ADMINPASSWORD");
        }
    await using (var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<DockmasterDb>>().CreateDbContextAsync())
        if (!await db.Users.AnyAsync())
            app.Logger.LogWarning("first run: open the UI and enter the setup code {Code} to create the admin (or set DOCKMASTER_ADMINPASSWORD)",
                app.Services.GetRequiredService<SetupCode>().Value);
}
app.Logger.LogInformation("UI on :{ui}, agents on :{agent} (TLS fingerprint {fp})", uiPort, agentPort, certs.FingerprintPretty);

// The agent port serves only the agent gRPC endpoint, the UI port everything else. Decided by the socket's local
// port, not by the Host header: behind docker port mapping (e.g. 9556:9443) agents send the external port.
// An agent keeps one stream open per connection; more at once are refused before anything is read, as nobody on the
// agent port has shown a token yet.
var agentStreams = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
const int MaxAgentStreams = 2;
app.Use(async (ctx, next) =>
{
    var onAgentPort = ctx.Connection.LocalPort == agentPort;
    var isAgentHub = ctx.Request.Path.StartsWithSegments("/dockmaster.AgentHub");
    if (onAgentPort != isAgentHub)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    if (!onAgentPort)
    {
        await next();
        return;
    }
    var id = ctx.Connection.Id;
    try
    {
        if (agentStreams.AddOrUpdate(id, 1, (_, n) => n + 1) > MaxAgentStreams)
        {
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }
        await next();
    }
    finally
    {
        if (agentStreams.AddOrUpdate(id, 0, (_, n) => n - 1) <= 0) agentStreams.TryRemove(KeyValuePair.Create(id, 0));
    }
});

// The hub opens shells in containers and the api changes everything: a page of another site must not reach them with
// the browser's cookie, and SameSite does not stop one on another port of the same host. Behind a reverse proxy the
// Host it passes, or its X-Forwarded-Host, must be the address the browser uses.
var originWarned = 0;
app.Use((ctx, next) =>
{
    if (!CookieGuarded(ctx.Request) || BrowserSameOrigin(ctx.Request)) return next();
    if (Interlocked.Exchange(ref originWarned, 1) == 0)
        app.Logger.LogWarning("refused a {Path} request from {Origin} (fetch site {Site}, host {Host}, forwarded host {Forwarded}); behind a reverse proxy, pass the Host header or X-Forwarded-Host",
            ctx.Request.Path.Value, ctx.Request.Headers.Origin.ToString(), ctx.Request.Headers["Sec-Fetch-Site"].ToString(), ctx.Request.Host.Value,
            ctx.Request.Headers["X-Forwarded-Host"].ToString());
    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
    return Task.CompletedTask;
});

// failures the handlers did not expect still reach the UI as { error }, never as an empty 500
app.UseExceptionHandler(errors => errors.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, message) = ex switch
    {
        DbUpdateException => (StatusCodes.Status409Conflict, "the change conflicts with existing data (duplicate name?)"),
        BadHttpRequestException b => (b.StatusCode, b.Message),
        JsonException => (StatusCodes.Status400BadRequest, "invalid request"),
        _ => (StatusCodes.Status500InternalServerError, ex?.Message ?? "unexpected error"),
    };
    ctx.Response.StatusCode = status;
    await ctx.Response.WriteAsJsonAsync(new { error = message });
}));

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGrpcService<AgentHubService>();

// every endpoint says who may call it (Access.AccessRules); one that does not is for admins only
var api = app.MapGroup("/api");
api.AddEndpointFilter(AccessRules.Enforce);
api.MapAuthApi();
api.MapUsersApi();
api.MapAgentsApi();
api.MapProjectsApi();
api.MapRegistriesApi();
api.MapSettingsApi();
api.MapMeshApi();
api.MapSystemApi();
api.MapEventsApi();
api.MapHooksApi(); // no sign-in: the token in the URL is the key
api.MapTemplatesApi(); // templates and keys signed in; their instances also with an API key
app.MapHub<LiveHub>("/hub").RequireAuthorization();

// SPA fallback (React app built into wwwroot)
app.MapFallbackToFile("index.html");

app.Run();

// the origin's host and port are the request's Host, or one of the X-Forwarded-Host values
// Requests a page of another site could send with the browser's cookie: anything to the hub, and changes to the api.
// A bearer key and a webhook's token are not sent by the browser on its own, so those requests are not guarded.
static bool CookieGuarded(HttpRequest request)
{
    if (request.Path.StartsWithSegments("/hub")) return true;
    if (!request.Path.StartsWithSegments("/api") || HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
        return false;
    return !request.Path.StartsWithSegments("/api/hooks") && request.Headers.Authorization.Count == 0;
}

// The browser says where a request comes from: Sec-Fetch-Site when it is a current one (another port of the same host
// is "same-site"), and Origin on every change. A client that is no browser sends neither and has no cookie to abuse.
static bool BrowserSameOrigin(HttpRequest request)
{
    if (request.Headers["Sec-Fetch-Site"] is [{ Length: > 0 } site] && site is not ("same-origin" or "none")) return false;
    return request.Headers.Origin is not [{ Length: > 0 } origin] || SameOrigin(origin, request);
}

static bool SameOrigin(string origin, HttpRequest request)
{
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var o)) return false;
    // a proxy may pass the host without the port the browser used (nginx's $host); a browser cannot set these headers
    // on a cross-site request without a preflight, which is not answered
    var proxied = request.Headers.ContainsKey("X-Forwarded-For") || request.Headers.ContainsKey("X-Forwarded-Proto") || request.Headers.ContainsKey("X-Forwarded-Host");
    var hosts = request.Headers["X-Forwarded-Host"].SelectMany(h => (h ?? "").Split(',')).Select(h => h.Trim()).Append(request.Host.Value ?? "");
    return hosts.Select(HostString.FromUriComponent).Any(h => string.Equals(h.Host, o.Host, StringComparison.OrdinalIgnoreCase)
        && (h.Port is { } port ? port == o.Port : proxied || o.IsDefaultPort));
}
