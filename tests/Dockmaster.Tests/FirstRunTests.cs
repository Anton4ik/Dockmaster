using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Dockmaster.Server.Access;
using Dockmaster.Server.Data;

namespace Dockmaster.Tests;

/// <summary>A server with nobody in it yet: the setup page needs the code from the log.</summary>
public sealed class FirstRunTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "dockmaster-tests-" + Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<AccessService> _app = null!;

    public async Task InitializeAsync() => _app = await ServerFixture.StartAsync(_dataDir, null);

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Setup_needs_the_code_from_the_log()
    {
        var c = _app.CreateClient();
        var code = _app.Services.GetRequiredService<SetupCode>().Value;
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/auth/setup", new { name = "mallory", password = "password-999" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/auth/setup", new { name = "mallory", password = "password-999", code = "AAAA-AAAA-AAAA" })).StatusCode);
        await using (var db = await _app.Services.GetRequiredService<IDbContextFactory<DockmasterDb>>().CreateDbContextAsync())
            Assert.False(await db.Users.AnyAsync());

        // typed in lower case without dashes is the same code
        var typed = code.Replace("-", "").ToLowerInvariant();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/setup", new { name = "boss", password = "password-999", code = typed })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/auth/setup", new { name = "mallory", password = "password-999", code })).StatusCode);
    }

    [Fact]
    public async Task Wrong_passwords_for_one_name_are_stopped_for_a_while()
    {
        var c = _app.CreateClient();
        var code = _app.Services.GetRequiredService<SetupCode>().Value;
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/setup", new { name = "boss", password = "password-999", code })).StatusCode);
        for (var i = 0; i < LoginThrottle.Failures; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/auth/login", new { name = "boss", password = "guess-" + i })).StatusCode);
        // even the right password waits now; another name does not
        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.PostAsJsonAsync("/api/auth/login", new { name = "boss", password = "password-999" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/auth/login", new { name = "other", password = "password-999" })).StatusCode);
    }
}
