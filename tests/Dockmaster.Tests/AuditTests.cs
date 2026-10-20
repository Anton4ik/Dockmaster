using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

/// <summary>Who did what: the user in the project's history (deploys, rollbacks, actions) and in audit events (changes).</summary>
[Collection(ServerCollection.Name)]
public sealed class AuditTests(ServerFixture f)
{
    private const string Compose = "services:\n  web:\n    image: nginx\n";

    // ---- the project's history ----

    [Fact]
    public async Task A_deploy_from_the_page_records_who_started_it()
    {
        await using var hub = await f.Hub("operator");
        var since = await LastDeploymentAsync();
        await StreamAsync(hub, "Deploy", f.AlphaProject, new[] { "web" }, true);

        var d = await DeploymentAfterAsync(since);
        Assert.Equal("deploy web", d.Action);
        Assert.Equal("operator", d.Source);
        // and an audit event with the outcome (nobody is connected: it failed)
        var e = await LastAuditAsync("operator");
        Assert.Equal(f.AlphaProject, e.ProjectId);
        Assert.StartsWith("deploy web with pull — failed", e.Message);
    }

    [Fact]
    public async Task A_project_action_records_who_started_it()
    {
        await using var hub = await f.Hub("global");
        var since = await LastDeploymentAsync();
        await StreamAsync(hub, "ProjectAction", f.AlphaProject, "restart", null);

        var d = await DeploymentAfterAsync(since);
        Assert.Equal("restart", d.Action);
        Assert.Equal("global", d.Source);
        Assert.StartsWith("restart", (await LastAuditAsync("global")).Message);
    }

    [Fact]
    public async Task A_rollback_records_who_asked_for_it()
    {
        int target;
        await using (var db = await f.Db())
        {
            var p = await db.Projects.SingleAsync(x => x.Id == f.AlphaProject);
            // the files of then are the files of now: the project is the same after it
            var old = new DeploymentEntity { ProjectId = p.Id, Action = "deploy", Ok = true, FinishedAt = DateTime.UtcNow, ComposeYaml = p.ComposeYaml, EnvFile = p.EnvFile };
            db.Deployments.Add(old);
            await db.SaveChangesAsync();
            target = old.Id;
        }
        await using var hub = await f.Hub("dev");
        await StreamAsync(hub, "Rollback", f.AlphaProject, target);

        var d = await DeploymentAfterAsync(target);
        Assert.Equal($"dev: rollback to #{target}", d.Source);
        Assert.Equal($"rollback to #{target} — failed", (await LastAuditAsync("dev")).Message.Split(':')[0]);
    }

    [Fact]
    public void By_joins_the_user_and_what_started_the_run()
    {
        Assert.Equal("anna: rollback to #12", ProjectService.By("anna", "rollback to #12"));
        Assert.Equal("rollback to #12", ProjectService.By("", "rollback to #12"));
        Assert.Equal("anna", ProjectService.By("anna", ""));
    }

    // ---- changes as audit events ----

    [Fact]
    public async Task Adding_saving_and_deleting_a_project_name_the_user()
    {
        var dev = await f.Client("dev");
        var r = await dev.PostAsJsonAsync("/api/projects", new { name = "audited", agentId = f.Alpha, composeYaml = Compose, envFile = "", healthTimeoutSeconds = 60 });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        var id = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var added = await LastAuditAsync("dev");
        Assert.Equal(("added project audited", id), (added.Message, added.ProjectId));

        r = await dev.PutAsJsonAsync($"/api/projects/{id}", new { name = "audited", agentId = f.Alpha, composeYaml = Compose + "  db:\n    image: postgres\n", envFile = "A=1\n", healthTimeoutSeconds = 60 });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        Assert.Equal("saved project audited: compose file, .env", (await LastAuditAsync("dev")).Message);

        Assert.Equal(HttpStatusCode.OK, (await dev.DeleteAsync($"/api/projects/{id}")).StatusCode);
        var deleted = await LastAuditAsync("dev");
        // about the server: the project is gone, its server's people still see it
        Assert.Equal(("deleted project audited", null, f.Alpha), (deleted.Message, deleted.ProjectId, deleted.AgentId));
        Assert.Contains(await AuditMessagesAsync("viewer"), m => m == "dev: deleted project audited");
    }

    [Fact]
    public async Task Secrets_set_and_deleted_name_the_user_but_never_the_value()
    {
        var dev = await f.Client("dev");
        Assert.True((await dev.PutAsJsonAsync($"/api/projects/{f.AlphaProject}/secrets/AUDIT_KEY", new { value = "s3cr3t-value" })).IsSuccessStatusCode);
        var set = await LastAuditAsync("dev");
        Assert.Equal(("added secret AUDIT_KEY", f.AlphaProject), (set.Message, set.ProjectId));
        Assert.True((await dev.PutAsJsonAsync($"/api/projects/{f.AlphaProject}/secrets/AUDIT_KEY", new { value = "other-value" })).IsSuccessStatusCode);
        Assert.Equal("changed secret AUDIT_KEY", (await LastAuditAsync("dev")).Message);
        Assert.Equal(HttpStatusCode.OK, (await dev.DeleteAsync($"/api/projects/{f.AlphaProject}/secrets/AUDIT_KEY")).StatusCode);
        Assert.Equal("deleted secret AUDIT_KEY", (await LastAuditAsync("dev")).Message);

        await using var db = await f.Db();
        Assert.False(await db.Events.AnyAsync(e => e.Message.Contains("s3cr3t") || e.Message.Contains("other-value")));
    }

    [Fact]
    public async Task Changes_to_users_and_roles_name_the_admin_and_only_admins_see_them()
    {
        var admin = await f.Client("admin");
        var r = await admin.PostAsJsonAsync("/api/roles", new { name = "Auditors", permissions = new[] { "view", "deploy" } });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var roleId = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Equal("added role Auditors: view, deploy", (await LastAuditAsync("admin")).Message);

        r = await admin.PostAsJsonAsync("/api/users", new { name = "carol", password = ServerFixture.Password, isAdmin = false, disabled = false,
            grants = new[] { new { agentId = f.Alpha, roleId } } });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        var userId = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Equal("added user carol, roles Auditors on alpha", (await LastAuditAsync("admin")).Message);

        r = await admin.PutAsJsonAsync($"/api/users/{userId}", new { name = "carol", password = "another-password", isAdmin = false, disabled = true,
            grants = new[] { new { agentId = 0, roleId = f.RoleIds["Viewer"] } } });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        Assert.Equal("changed user carol: disabled, new password, roles now Viewer on all servers", (await LastAuditAsync("admin")).Message);

        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/users/{userId}")).StatusCode);
        Assert.Equal("deleted user carol", (await LastAuditAsync("admin")).Message);
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/roles/{roleId}")).StatusCode);
        Assert.Equal("deleted role Auditors", (await LastAuditAsync("admin")).Message);

        // about no server: the admins' alone
        Assert.Contains("admin: deleted user carol", await AuditMessagesAsync("admin"));
        Assert.DoesNotContain(await AuditMessagesAsync("global"), m => m.Contains("carol"));
    }

    [Fact]
    public async Task Removing_a_server_names_the_admin()
    {
        int id;
        await using (var db = await f.Db())
        {
            var a = new AgentEntity { Name = "doomed", TokenHash = "d" };
            db.Agents.Add(a);
            await db.SaveChangesAsync();
            id = a.Id;
        }
        Assert.Equal(HttpStatusCode.OK, (await (await f.Client("admin")).DeleteAsync($"/api/agents/{id}")).StatusCode);
        var e = await LastAuditAsync("admin");
        Assert.Equal(("removed server doomed", null), (e.Message, e.AgentId));
    }

    [Fact]
    public async Task The_events_list_carries_the_user_and_filters_by_kind()
    {
        var dev = await f.Client("dev");
        Assert.True((await dev.PutAsJsonAsync($"/api/projects/{f.AlphaProject}/secrets/LISTED", new { value = "x" })).IsSuccessStatusCode);
        await dev.DeleteAsync($"/api/projects/{f.AlphaProject}/secrets/LISTED");
        await using (var db = await f.Db())
        {
            db.Events.Add(new EventEntity { Level = "warning", Kind = "test", Message = "not an audit one", AgentId = f.Alpha });
            await db.SaveChangesAsync();
        }
        var list = (await (await f.Client("admin")).GetFromJsonAsync<JsonElement>("/api/events?kind=audit&limit=500")).EnumerateArray().ToList();
        Assert.All(list, e => Assert.Equal("audit", e.GetProperty("kind").GetString()));
        var deleted = list.First(e => e.GetProperty("message").GetString() == "deleted secret LISTED");
        Assert.Equal("dev", deleted.GetProperty("user").GetString());
        Assert.Equal("shop", deleted.GetProperty("project").GetString());
    }

    // ---- helpers ----

    [Fact]
    public async Task A_service_edited_through_its_form_names_the_user_and_the_projects_server()
    {
        var dev = await f.Client("dev");
        var r = await dev.PostAsJsonAsync("/api/projects", new { name = "formed", agentId = f.Alpha, composeYaml = Compose, envFile = "", healthTimeoutSeconds = 60 });
        var id = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        r = await dev.PutAsJsonAsync($"/api/projects/{id}/services/web/expose", new { ports = new[] { "8080" } });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        var e = await LastAuditAsync("dev");
        Assert.Equal("project formed: set the ports web offers", e.Message);
        // about the project's server too: still there for the server's people once the project is gone
        Assert.Equal((id, f.Alpha), (e.ProjectId, e.AgentId));

        // the same again changes nothing, and says nothing
        await dev.PutAsJsonAsync($"/api/projects/{id}/services/web/expose", new { ports = new[] { "8080" } });
        Assert.Equal(e.Id, (await LastAuditAsync("dev")).Id);

        r = await dev.PutAsJsonAsync($"/api/projects/{id}/files/app.conf", new { content = "x" });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        Assert.Equal("added file app.conf", (await LastAuditAsync("dev")).Message);
        await dev.DeleteAsync($"/api/projects/{id}");
    }

    [Fact]
    public async Task What_an_api_key_does_is_in_the_events_under_its_name()
    {
        var admin = await f.Client("admin");
        var r = await admin.PostAsJsonAsync("/api/templates", new { name = "keyed", composeYaml = "services:\n  app:\n    image: nginx\n", maxInstances = 0, healthTimeoutSeconds = 60 });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        var templateId = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Equal("added template keyed", (await LastAuditAsync("admin")).Message);
        r = await admin.PostAsJsonAsync("/api/apikeys", new { name = "backend", templateIds = new[] { templateId } });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        var key = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString();
        Assert.Equal("made API key backend for keyed", (await LastAuditAsync("admin")).Message);

        var create = new HttpRequestMessage(HttpMethod.Post, "/api/templates/keyed/instances") { Content = JsonContent.Create(new { name = "one", server = "alpha", deploy = false }) };
        create.Headers.Authorization = new("Bearer", key);
        r = await f.Anonymous().SendAsync(create);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        var e = await LastAuditAsync("API key backend");
        Assert.Equal("added instance one of template keyed", e.Message);
        Assert.Equal(f.Alpha, e.AgentId);
    }

    private async Task<int> LastDeploymentAsync()
    {
        await using var db = await f.Db();
        return await db.Deployments.MaxAsync(d => (int?)d.Id) ?? 0;
    }

    private async Task<DeploymentEntity> DeploymentAfterAsync(int id)
    {
        await using var db = await f.Db();
        return await db.Deployments.AsNoTracking().Where(d => d.Id > id && d.ProjectId == f.AlphaProject).OrderByDescending(d => d.Id).FirstAsync();
    }

    private async Task<EventEntity> LastAuditAsync(string user)
    {
        await using var db = await f.Db();
        var e = await db.Events.AsNoTracking().Where(x => x.Kind == DmEvent.Audit && x.User == user).OrderByDescending(x => x.Id).FirstOrDefaultAsync();
        Assert.NotNull(e);
        Assert.Equal(DmEvent.Info, e.Level);
        return e;
    }

    /// <summary>The audit events the user sees, as "who: what".</summary>
    private async Task<List<string>> AuditMessagesAsync(string user) =>
        [.. (await (await f.Client(user)).GetFromJsonAsync<JsonElement>("/api/events?kind=audit&limit=500")).EnumerateArray()
            .Select(e => $"{e.GetProperty("user").GetString()}: {e.GetProperty("message").GetString()}")];

    private static async Task<List<string>> StreamAsync(HubConnection hub, string method, params object?[] args)
    {
        var lines = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var line in hub.StreamAsyncCore<string>(method, args, cts.Token)) lines.Add(line);
        Assert.StartsWith("\u0001", lines.Last());
        return lines;
    }
}
