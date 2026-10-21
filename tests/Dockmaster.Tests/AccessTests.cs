using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Dockmaster.Server.Access;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

/// <summary>Signing in, what each user gets to see, and changes to users and roles taking effect at once.</summary>
[Collection(ServerCollection.Name)]
public sealed class AccessTests(ServerFixture f)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ---- signing in ----

    [Fact]
    public async Task A_wrong_password_an_unknown_name_and_a_disabled_account_do_not_sign_in()
    {
        var c = f.Anonymous();
        var wrong = await c.PostAsJsonAsync("/api/auth/login", new { name = "viewer", password = "nope-nope" });
        var unknown = await c.PostAsJsonAsync("/api/auth/login", new { name = "who", password = ServerFixture.Password });
        var disabled = await c.PostAsJsonAsync("/api/auth/login", new { name = "gone", password = ServerFixture.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        // the same words for both: nothing to learn about who exists
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, disabled.StatusCode);
        Assert.Contains("disabled", await disabled.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/agents")).StatusCode);
    }

    [Fact]
    public async Task A_sign_in_without_a_name_is_the_admins_and_names_ignore_case()
    {
        var c = f.Anonymous();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/login", new { password = ServerFixture.AdminPassword })).StatusCode);
        var me = await c.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.True(me.GetProperty("user").GetProperty("isAdmin").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await f.Anonymous().PostAsJsonAsync("/api/auth/login", new { name = "VIEWER", password = ServerFixture.Password })).StatusCode);
    }

    [Fact]
    public async Task Setup_is_refused_once_anyone_can_sign_in()
    {
        var r = await f.Anonymous().PostAsJsonAsync("/api/auth/setup", new { name = "mallory", password = "password-999" });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        await using var db = await f.Db();
        Assert.False(await db.Users.AnyAsync(u => u.Name == "mallory"));
    }

    [Fact]
    public async Task Me_tells_the_ui_what_the_user_may_do_where()
    {
        var me = await (await f.Client("dev")).GetFromJsonAsync<JsonElement>("/api/auth/me");
        var user = me.GetProperty("user");
        Assert.False(user.GetProperty("isAdmin").GetBoolean());
        Assert.Empty(user.GetProperty("everywhere").EnumerateArray());
        var servers = user.GetProperty("servers");
        Assert.Equal(["view", "control", "deploy", "edit", "secrets", "terminal", "volumes"], Strings(servers.GetProperty(f.Alpha.ToString())));
        Assert.Equal(["view"], Strings(servers.GetProperty(f.Beta.ToString())));

        var global = (await (await f.Client("global")).GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("user");
        Assert.Equal(["view", "control", "deploy"], Strings(global.GetProperty("everywhere")));
    }

    // ---- what each one sees ----

    [Fact]
    public async Task The_server_list_has_only_the_users_servers()
    {
        Assert.Equal(["alpha"], await NamesAsync("viewer", "/api/agents"));
        Assert.Equal(["alpha", "beta"], await NamesAsync("dev", "/api/agents"));
        Assert.Empty(await NamesAsync("nobody", "/api/agents"));
        // every server, also ones added later
        Assert.Superset(new HashSet<string> { "alpha", "beta", "gamma" }, (await NamesAsync("global", "/api/agents")).ToHashSet());
    }

    [Fact]
    public async Task The_project_list_has_only_the_projects_of_the_users_servers()
    {
        Assert.Equal(["shop"], await NamesAsync("viewer", "/api/projects"));
        Assert.Contains("blog", await NamesAsync("dev", "/api/projects"));
        Assert.Empty(await NamesAsync("nobody", "/api/projects"));
    }

    [Fact]
    public async Task The_env_file_is_only_shown_with_the_secrets_permission()
    {
        var seen = await (await f.Client("viewer")).GetFromJsonAsync<JsonElement>($"/api/projects/{f.AlphaProject}");
        Assert.Equal("", seen.GetProperty("envFile").GetString());
        Assert.True(seen.GetProperty("envHidden").GetBoolean());
        var dev = await (await f.Client("dev")).GetFromJsonAsync<JsonElement>($"/api/projects/{f.AlphaProject}");
        Assert.Contains("DB_PASSWORD", dev.GetProperty("envFile").GetString());
        Assert.False(dev.GetProperty("envHidden").GetBoolean());
        // dev sees blog on beta, but has no secrets there
        var blog = await (await f.Client("dev")).GetFromJsonAsync<JsonElement>($"/api/projects/{f.BetaProject}");
        Assert.True(blog.GetProperty("envHidden").GetBoolean());
    }

    [Fact]
    public async Task Events_are_only_those_of_the_users_servers_and_their_projects()
    {
        await using (var db = await f.Db())
        {
            db.Events.AddRange(
                new EventEntity { Level = "warning", Kind = "test", Message = "matrix: on alpha", AgentId = f.Alpha },
                new EventEntity { Level = "warning", Kind = "test", Message = "matrix: shop", ProjectId = f.AlphaProject },
                new EventEntity { Level = "warning", Kind = "test", Message = "matrix: on beta", AgentId = f.Beta },
                new EventEntity { Level = "warning", Kind = "test", Message = "matrix: nowhere" });
            await db.SaveChangesAsync();
        }
        async Task<List<string>> Messages(string user) =>
            [.. (await (await f.Client(user)).GetFromJsonAsync<JsonElement>("/api/events?limit=500")).EnumerateArray()
                .Select(e => e.GetProperty("message").GetString()!).Where(m => m.StartsWith("matrix:"))];
        Assert.Equal(["matrix: shop", "matrix: on alpha"], await Messages("viewer"));
        Assert.Empty(await Messages("nobody"));
        Assert.Equal(4, (await Messages("admin")).Count);
    }

    [Fact]
    public async Task The_mesh_shows_only_the_users_servers()
    {
        var mesh = await (await f.Client("viewer")).GetFromJsonAsync<JsonElement>("/api/mesh");
        Assert.All(mesh.GetProperty("nodes").EnumerateArray(), n => Assert.Equal(f.Alpha, n.GetProperty("agentId").GetInt32()));
    }

    [Fact]
    public void Container_env_values_are_hidden_without_the_secrets_permission()
    {
        // the agent's answer, as it would come: names stay, values go
        var payload = """{"name":"web-1","env":[{"name":"DB_PASSWORD","value":"hunter2","fromImage":false}]}""";
        var text = Server.Api.ContainersApi.WithoutEnvValues(payload);
        Assert.DoesNotContain("hunter2", text);
        Assert.Contains("DB_PASSWORD", text);
        Assert.Contains("\"envHidden\":true", text);
    }

    // ---- projects across servers ----

    [Fact]
    public async Task A_service_cannot_be_put_on_a_server_the_user_may_not_edit_on()
    {
        var dev = await f.Client("dev");
        var onBeta = "services:\n  web:\n    image: nginx\n    x-dockmaster:\n      agent: beta\n";
        var r = await dev.PutAsJsonAsync($"/api/projects/{f.AlphaProject}", new { name = "shop", agentId = f.Alpha, composeYaml = onBeta, envFile = "DB_PASSWORD=hunter2\n", healthTimeoutSeconds = 60 });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains("beta", await r.Content.ReadAsStringAsync());
        // moving the whole project there neither
        r = await dev.PutAsJsonAsync($"/api/projects/{f.AlphaProject}", new { name = "shop", agentId = f.Beta, composeYaml = "services:\n  web:\n    image: nginx\n", envFile = "", healthTimeoutSeconds = 60 });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        await using var db = await f.Db();
        var shop = await db.Projects.SingleAsync(p => p.Id == f.AlphaProject);
        Assert.Equal(f.Alpha, shop.AgentId);
        Assert.DoesNotContain("beta", shop.ComposeYaml);
    }

    [Fact]
    public async Task A_project_can_only_be_made_on_a_server_the_user_edits_projects_on()
    {
        var dev = await f.Client("dev");
        var compose = "services:\n  web:\n    image: nginx\n";
        Assert.Equal(HttpStatusCode.Forbidden, (await dev.PostAsJsonAsync("/api/projects", new { name = "devonbeta", agentId = f.Beta, composeYaml = compose, envFile = "", healthTimeoutSeconds = 60 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await f.Client("operator")).PostAsJsonAsync("/api/projects", new { name = "oponalpha", agentId = f.Alpha, composeYaml = compose, envFile = "", healthTimeoutSeconds = 60 })).StatusCode);
        var made = await dev.PostAsJsonAsync("/api/projects", new { name = "devonalpha", agentId = f.Alpha, composeYaml = compose, envFile = "", healthTimeoutSeconds = 60 });
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var id = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Forbidden, (await (await f.Client("viewer")).DeleteAsync($"/api/projects/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await dev.DeleteAsync($"/api/projects/{id}")).StatusCode);
    }

    [Fact]
    public async Task Without_the_secrets_permission_a_save_keeps_the_env_file()
    {
        // a role that edits but has no secrets
        var admin = await f.Client("admin");
        var role = await CreateRoleAsync(admin, "editor-no-secrets", [Perm.Edit]);
        await CreateUserAsync(admin, "editor1", new { agentId = f.Alpha, roleId = role });
        var c = await SignInAsync("editor1");
        var r = await c.PutAsJsonAsync($"/api/projects/{f.AlphaProject}", new { name = "shop", agentId = f.Alpha, composeYaml = "services:\n  web:\n    image: nginx\n", envFile = "", healthTimeoutSeconds = 60 });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        await using var db = await f.Db();
        Assert.Equal("DB_PASSWORD=hunter2\n", (await db.Projects.SingleAsync(p => p.Id == f.AlphaProject)).EnvFile);

        // what a change answers with is the project as the user may see it: not the .env
        var saved = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("", saved.GetProperty("envFile").GetString());
        Assert.True(saved.GetProperty("envHidden").GetBoolean());
        r = await c.PutAsJsonAsync($"/api/projects/{f.AlphaProject}/services/web/secrets", new { names = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.DoesNotContain("hunter2", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Without_the_secrets_permission_the_environment_and_files_are_hidden_and_kept()
    {
        const string compose = "services:\n  web:\n    image: nginx\n    environment:\n      PASSWORD: hunter3   # inline\n      TZ: UTC\n";
        int id;
        await using (var db = await f.Db())
        {
            var vault = new ProjectEntity { Name = "vault", AgentId = f.Alpha, ComposeYaml = compose };
            db.Projects.Add(vault);
            await db.SaveChangesAsync();
            id = vault.Id;
            db.ProjectFiles.Add(new ProjectFileEntity { ProjectId = id, Name = "app.conf", Content = "secret=s3" });
            db.Deployments.Add(new DeploymentEntity { ProjectId = id, Action = "deploy", ComposeYaml = compose, EnvFile = "" });
            await db.SaveChangesAsync();
        }
        try
        {
            var viewer = await f.Client("viewer");
            var seen = await viewer.GetStringAsync($"/api/projects/{id}");
            Assert.DoesNotContain("hunter3", seen);
            Assert.DoesNotContain("UTC", seen);
            Assert.Contains("PASSWORD: (hidden)   # inline", JsonDocument.Parse(seen).RootElement.GetProperty("composeYaml").GetString());
            var files = await viewer.GetStringAsync($"/api/projects/{id}/files");
            Assert.DoesNotContain("s3", files);
            Assert.Contains("\"hidden\":true", files);
            var depId = (await viewer.GetFromJsonAsync<JsonElement>($"/api/projects/{id}/deployments"))[0].GetProperty("id").GetInt32();
            Assert.DoesNotContain("hunter3", await viewer.GetStringAsync($"/api/projects/{id}/deployments/{depId}/files"));
            Assert.Contains("hunter3", await (await f.Client("dev")).GetStringAsync($"/api/projects/{id}"));

            // an editor without secrets saves what they saw: the stored values stay, their change goes in
            var admin = await f.Client("admin");
            var role = await CreateRoleAsync(admin, "editor-env", [Perm.View, Perm.Edit]);
            await CreateUserAsync(admin, "editor2", new { agentId = f.Alpha, roleId = role });
            var editor = await SignInAsync("editor2");
            var shown = (await editor.GetFromJsonAsync<JsonElement>($"/api/projects/{id}")).GetProperty("composeYaml").GetString()!;
            var r = await editor.PutAsJsonAsync($"/api/projects/{id}", new { name = "vault", agentId = f.Alpha, composeYaml = shown.Replace("image: nginx", "image: nginx:1.27"), envFile = "", healthTimeoutSeconds = 60 });
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.DoesNotContain("hunter3", await r.Content.ReadAsStringAsync());
            await using (var db = await f.Db())
                Assert.Equal(compose.Replace("image: nginx", "image: nginx:1.27"), (await db.Projects.SingleAsync(p => p.Id == id)).ComposeYaml);

            r = await editor.PutAsJsonAsync($"/api/projects/{id}/services/web/settings",
                new { environment = new[] { new { name = "PASSWORD", value = "(hidden)" }, new { name = "TZ", value = "Europe/Berlin" } } });
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.DoesNotContain("hunter3", await r.Content.ReadAsStringAsync());
            await using (var db = await f.Db())
            {
                var env = ComposeServices.Parse((await db.Projects.SingleAsync(p => p.Id == id)).ComposeYaml).Single().Environment!;
                Assert.Equal(["hunter3", "Europe/Berlin"], env.Select(e => e.Value));
            }

            // a file they cannot see is not theirs to write over; a new one is
            Assert.Equal(HttpStatusCode.Forbidden, (await editor.PutAsJsonAsync($"/api/projects/{id}/files/app.conf", new { content = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await editor.PutAsJsonAsync($"/api/projects/{id}/files/new.conf", new { content = "x" })).StatusCode);
        }
        finally
        {
            await using var db = await f.Db();
            await ProjectService.DeleteRowsAsync(db, id, CancellationToken.None);
        }
    }

    // ---- users and roles ----

    [Fact]
    public async Task A_new_permission_applies_at_once_and_one_taken_away_too()
    {
        var admin = await f.Client("admin");
        var id = await CreateUserAsync(admin, "temp-op", new { agentId = f.Alpha, roleId = f.RoleIds["Viewer"] });
        var c = await SignInAsync("temp-op");
        var stop = $"/api/agents/{f.Alpha}/containers/web-1/stop";
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync(stop, null)).StatusCode);

        await UpdateUserAsync(admin, id, "temp-op", new { agentId = f.Alpha, roleId = f.RoleIds["Operator"] });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await c.PostAsync(stop, null)).StatusCode); // allowed: the server is just not connected

        await UpdateUserAsync(admin, id, "temp-op");
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync(stop, null)).StatusCode); // the server is not theirs any more
    }

    [Fact]
    public async Task A_changed_role_applies_to_everyone_who_has_it()
    {
        var admin = await f.Client("admin");
        var role = await CreateRoleAsync(admin, "stoppers", [Perm.View]);
        await CreateUserAsync(admin, "stopper", new { agentId = f.Alpha, roleId = role });
        var c = await SignInAsync("stopper");
        var stop = $"/api/agents/{f.Alpha}/containers/web-1/stop";
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync(stop, null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/roles/{role}", new { name = "stoppers", permissions = new[] { Perm.Control } })).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await c.PostAsync(stop, null)).StatusCode);
    }

    [Fact]
    public async Task Disabling_a_user_or_setting_their_password_ends_their_sessions()
    {
        var admin = await f.Client("admin");
        var id = await CreateUserAsync(admin, "leaver", new { agentId = f.Alpha, roleId = f.RoleIds["Viewer"] });
        var c = await SignInAsync("leaver");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/agents")).StatusCode);
        await UpdateUserAsync(admin, id, "leaver", password: "new-password-1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/agents")).StatusCode);

        c = await SignInAsync("leaver", "new-password-1");
        await admin.PutAsJsonAsync($"/api/users/{id}", new { name = "leaver", isAdmin = false, disabled = true });
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/agents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Anonymous().PostAsJsonAsync("/api/auth/login", new { name = "leaver", password = "new-password-1" })).StatusCode);
    }

    [Fact]
    public async Task A_deleted_users_session_ends()
    {
        var admin = await f.Client("admin");
        var id = await CreateUserAsync(admin, "deleted1", new { agentId = f.Alpha, roleId = f.RoleIds["Viewer"] });
        var c = await SignInAsync("deleted1");
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/users/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/agents")).StatusCode);
        await using var db = await f.Db();
        Assert.False(await db.UserGrants.AnyAsync(g => g.UserId == id));
    }

    [Fact]
    public async Task Ones_own_new_password_keeps_this_session_and_ends_the_others()
    {
        var admin = await f.Client("admin");
        await CreateUserAsync(admin, "changer", new { agentId = f.Alpha, roleId = f.RoleIds["Viewer"] });
        var here = await SignInAsync("changer");
        var elsewhere = await SignInAsync("changer");
        Assert.Equal(HttpStatusCode.BadRequest, (await here.PostAsJsonAsync("/api/auth/password", new { current = "wrong-one", password = "changed-pw-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await here.PostAsJsonAsync("/api/auth/password", new { current = ServerFixture.Password, password = "changed-pw-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await here.GetAsync("/api/agents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await elsewhere.GetAsync("/api/agents")).StatusCode);
    }

    [Fact]
    public async Task What_a_project_allows_counts_every_server_it_touches()
    {
        int id;
        await using (var db = await f.Db())
        {
            // dev develops on alpha, only views beta: a project on alpha with a service on beta is not theirs to change
            var p = new ProjectEntity { Name = "split", AgentId = f.Alpha, EnvFile = "KEY=secret\n",
                ComposeYaml = "services:\n  web:\n    image: nginx\n  db:\n    image: postgres\n    x-dockmaster:\n      agent: beta\n" };
            db.Projects.Add(p);
            await db.SaveChangesAsync();
            id = p.Id;
        }
        var dev = await f.Client("dev");
        var seen = await dev.GetFromJsonAsync<JsonElement>($"/api/projects/{id}");
        Assert.Equal(["view"], Strings(seen.GetProperty("may")));
        Assert.True(seen.GetProperty("envHidden").GetBoolean());
        // and a save does not overwrite the .env it was not shown
        var r = await dev.PutAsJsonAsync($"/api/projects/{id}", new { name = "split", agentId = f.Alpha, composeYaml = "services:\n  web:\n    image: nginx\n", envFile = "", healthTimeoutSeconds = 60 });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        await using (var db = await f.Db()) Assert.Equal("KEY=secret\n", (await db.Projects.SingleAsync(p => p.Id == id)).EnvFile);

        var mine = await dev.GetFromJsonAsync<JsonElement>($"/api/projects/{f.AlphaProject}");
        Assert.Contains("edit", Strings(mine.GetProperty("may")));
        Assert.Contains("secrets", Strings(mine.GetProperty("may")));

        // the volumes of a server the user does not see are not listed
        var volumes = await (await f.Client("viewer")).GetFromJsonAsync<JsonElement>($"/api/projects/{id}/volumes");
        var beta = volumes.GetProperty("servers").EnumerateArray().Single(s => s.GetProperty("agentId").GetInt32() == f.Beta);
        Assert.Contains("do not see", beta.GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_admins_own_password_is_changed_only_with_the_current_one()
    {
        var admin = await f.Client("admin");
        var r = await admin.PostAsJsonAsync("/api/users", new { name = "admin2", password = ServerFixture.Password, isAdmin = true, disabled = false, grants = Array.Empty<object>() });
        var id = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var here = await SignInAsync("admin2");
        var elsewhere = await SignInAsync("admin2");
        // the users page does not do it: a session left open is not enough
        r = await here.PutAsJsonAsync($"/api/users/{id}", new { name = "admin2", password = "admin2-new-pw", isAdmin = true, disabled = false, grants = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await elsewhere.GetAsync("/api/agents")).StatusCode);
        // the account dialog does, and the other sessions end
        r = await here.PostAsJsonAsync("/api/auth/password", new { current = ServerFixture.Password, password = "admin2-new-pw" });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await here.GetAsync("/api/agents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await elsewhere.GetAsync("/api/agents")).StatusCode);
    }

    [Fact]
    public async Task Server_names_differ_in_more_than_case()
    {
        var admin = await f.Client("admin");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/agents/{f.Gamma}", new { name = "ALPHA" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/agents", new { name = "Beta", publicUrl = "https://example.test:5001" })).StatusCode);
    }

    [Fact]
    public async Task An_admin_cannot_lock_themselves_out()
    {
        var admin = await f.Client("admin");
        var me = f.UserIds["admin"];
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/users/{me}", new { name = "admin", isAdmin = false, disabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/users/{me}", new { name = "admin", isAdmin = true, disabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync($"/api/users/{me}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task User_and_role_forms_are_checked()
    {
        var admin = await f.Client("admin");
        async Task<HttpStatusCode> NewUser(object body) => (await admin.PostAsJsonAsync("/api/users", body)).StatusCode;
        Assert.Equal(HttpStatusCode.Conflict, await NewUser(new { name = "Viewer", password = "password-1", isAdmin = false, disabled = false }));
        Assert.Equal(HttpStatusCode.BadRequest, await NewUser(new { name = "two words", password = "password-1", isAdmin = false, disabled = false }));
        Assert.Equal(HttpStatusCode.BadRequest, await NewUser(new { name = "shortpw", password = "short", isAdmin = false, disabled = false }));
        Assert.Equal(HttpStatusCode.BadRequest, await NewUser(new { name = "noserver", password = "password-1", grants = new[] { new { agentId = 9999, roleId = f.RoleIds["Viewer"] } } }));
        Assert.Equal(HttpStatusCode.BadRequest, await NewUser(new { name = "norole", password = "password-1", grants = new[] { new { agentId = f.Alpha, roleId = 9999 } } }));
        Assert.Equal(HttpStatusCode.BadRequest, await NewUser(new { name = "twice", password = "password-1",
            grants = new[] { new { agentId = f.Alpha, roleId = f.RoleIds["Viewer"] }, new { agentId = f.Alpha, roleId = f.RoleIds["Operator"] } } }));

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/roles", new { name = "x", permissions = new[] { "root" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/roles", new { name = "VIEWER", permissions = new[] { "view" } })).StatusCode);
        // view comes with everything
        var made = await (await admin.PostAsJsonAsync("/api/roles", new { name = "shell only", permissions = new[] { "terminal" } })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["view", "terminal"], Strings(made.GetProperty("permissions")));
    }

    [Fact]
    public async Task A_role_in_use_cannot_be_deleted()
    {
        var admin = await f.Client("admin");
        var r = await admin.DeleteAsync($"/api/roles/{f.RoleIds["Developer"]}");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("dev", await r.Content.ReadAsStringAsync());
        var unused = await CreateRoleAsync(admin, "unused", [Perm.View]);
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/roles/{unused}")).StatusCode);
    }

    [Fact]
    public async Task A_deleted_server_takes_its_grants_along()
    {
        int temp;
        await using (var db = await f.Db())
        {
            var a = new AgentEntity { Name = "temporary", TokenHash = "t" };
            db.Agents.Add(a);
            await db.SaveChangesAsync();
            temp = a.Id;
        }
        var admin = await f.Client("admin");
        var user = await CreateUserAsync(admin, "on-temp", new { agentId = temp, roleId = f.RoleIds["Viewer"] });
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/agents/{temp}")).StatusCode);
        await using var check = await f.Db();
        Assert.False(await check.UserGrants.AnyAsync(g => g.UserId == user));
    }

    [Fact]
    public async Task Template_instances_by_hand_are_for_admins_api_keys_still_work_without_a_session()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await (await f.Client("global")).GetAsync("/api/templates/game/instances")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await (await f.Client("admin")).GetAsync("/api/templates/game/instances")).StatusCode);
        var key = new HttpRequestMessage(HttpMethod.Get, "/api/templates/game/instances");
        key.Headers.Authorization = new("Bearer", "dmk_unknown");
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Anonymous().SendAsync(key)).StatusCode);
    }

    // ---- the hub ----

    [Fact]
    public async Task Hub_commands_check_the_permission_before_they_run()
    {
        await using var viewer = await f.Hub("viewer");
        var refused = await StreamAsync(viewer, "Deploy", f.AlphaProject, null, false);
        Assert.StartsWith("\u0001fail you have no deploy permission", refused.Last());
        Assert.StartsWith("\u0001fail", (await StreamAsync(viewer, "ProjectAction", f.AlphaProject, "stop", null)).Last());
        Assert.Contains("not found", (await StreamAsync(viewer, "Deploy", f.BetaProject, null, false)).Last());
        Assert.Contains("only an admin", (await StreamAsync(viewer, "MeshTune")).Last());
        Assert.Contains("terminal", (await StreamAsync(viewer, "Exec", f.Alpha, "web-1", 80, 24)).Last());
        Assert.Contains("not found", (await StreamAsync(viewer, "ContainerLogs", f.Beta, "web-1", 10)).Last());
        await Assert.ThrowsAsync<HubException>(() => viewer.InvokeAsync("WatchAgent", f.Beta));
        await viewer.InvokeAsync("WatchAgent", f.Alpha);

        // allowed: it gets as far as the server, which is not connected
        await using var op = await f.Hub("operator");
        var deploy = await StreamAsync(op, "Deploy", f.AlphaProject, null, false);
        Assert.DoesNotContain("permission", deploy.Last());
    }

    [Fact]
    public async Task A_rollback_takes_secrets_and_the_servers_of_the_deployment_gone_back_to()
    {
        int onBeta;
        await using (var db = await f.Db())
        {
            // a deploy of then that had the service on beta
            var d = new DeploymentEntity
            {
                ProjectId = f.AlphaProject, Action = "deploy", Ok = true, FinishedAt = DateTime.UtcNow, EnvFile = "",
                ComposeYaml = "services:\n  web:\n    image: nginx\n    x-dockmaster:\n      agent: beta\n",
            };
            db.Deployments.Add(d);
            await db.SaveChangesAsync();
            onBeta = d.Id;
        }
        // the operator deploys on alpha, but the .env a rollback puts back is a secret
        await using var op = await f.Hub("operator");
        Assert.Contains("secrets permission", (await StreamAsync(op, "Rollback", f.AlphaProject, onBeta)).Last(), StringComparison.OrdinalIgnoreCase);
        // dev has deploy and secrets on alpha, only view on beta
        await using var dev = await f.Hub("dev");
        Assert.Contains("deploy permission on beta", (await StreamAsync(dev, "Rollback", f.AlphaProject, onBeta)).Last());
    }

    [Fact]
    public async Task A_users_live_connection_ends_when_their_access_changes()
    {
        var admin = await f.Client("admin");
        var id = await CreateUserAsync(admin, "watched", new { agentId = f.Alpha, roleId = f.RoleIds["Viewer"] });
        await using var hub = await f.Hub("watched");
        var closed = new TaskCompletionSource();
        hub.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        await UpdateUserAsync(admin, id, "watched", new { agentId = f.Alpha, roleId = f.RoleIds["Operator"] });
        // the browser connects again and is checked anew (the UI reads what it may do then)
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Server_updates_reach_only_the_users_who_see_the_server()
    {
        await using var viewer = await f.Hub("viewer");
        var seen = new List<int>();
        viewer.On<JsonElement>("agent", a => { lock (seen) seen.Add(a.GetProperty("id").GetInt32()); });
        var reg = f.App.Services.GetRequiredService<AgentRegistry>();
        for (var i = 0; i < 50 && !Seen(); i++)
        {
            reg.Notify(f.Beta);
            reg.Notify(f.Alpha);
            await Task.Delay(100);
        }
        Assert.True(Seen(), "alpha's update did not come");
        lock (seen) Assert.DoesNotContain(f.Beta, seen);
        bool Seen() { lock (seen) return seen.Contains(f.Alpha); }
    }

    // ---- helpers ----

    private static async Task<List<string>> StreamAsync(HubConnection hub, string method, params object?[] args)
    {
        var lines = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await foreach (var line in hub.StreamAsyncCore<string>(method, args, cts.Token)) lines.Add(line);
        return lines;
    }

    private async Task<List<string>> NamesAsync(string user, string path) =>
        [.. (await (await f.Client(user)).GetFromJsonAsync<JsonElement>(path)).EnumerateArray().Select(x => x.GetProperty("name").GetString()!)];

    private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(x => x.GetString()!)];

    private static async Task<int> CreateRoleAsync(HttpClient admin, string name, string[] permissions)
    {
        var r = await admin.PostAsJsonAsync("/api/roles", new { name, permissions });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static async Task<int> CreateUserAsync(HttpClient admin, string name, params object[] grants)
    {
        var r = await admin.PostAsJsonAsync("/api/users", new { name, password = ServerFixture.Password, isAdmin = false, disabled = false, grants });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static async Task UpdateUserAsync(HttpClient admin, int id, string name, params object[] grants) =>
        await UpdateUserAsync(admin, id, name, null, grants);

    private static async Task UpdateUserAsync(HttpClient admin, int id, string name, string? password, params object[] grants)
    {
        var r = await admin.PutAsJsonAsync($"/api/users/{id}", new { name, password, isAdmin = false, disabled = false, grants });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
    }

    private async Task<HttpClient> SignInAsync(string name, string password = ServerFixture.Password)
    {
        var c = f.Anonymous();
        var r = await c.PostAsJsonAsync("/api/auth/login", new { name, password });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return c;
    }
}
