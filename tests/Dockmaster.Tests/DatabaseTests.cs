using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

public class DatabaseTests
{
    /// <summary>The model builds, a new database is created and upgraded twice (every step a no-op the second time).</summary>
    [Fact]
    public async Task A_new_database_is_created_and_upgraded()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = new DockmasterDb(new DbContextOptionsBuilder<DockmasterDb>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        await db.UpgradeSchemaAsync();
        await db.UpgradeSchemaAsync();

        var p = new ProjectEntity { Name = "shop", ComposeYaml = "services: {}" };
        p.Extras = new() { ["api"] = new Dictionary<int, int> { [2] = 1, [3] = 0 } };
        db.Projects.Add(p);
        db.Events.Add(new EventEntity { Level = "warning", Kind = "test", Message = "m", ProjectId = 1 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var back = await db.Projects.SingleAsync();
        Assert.Equal("api=2:1", back.AutoscaleExtra);
        Assert.Equal(1, back.Extras["api"][2]);
        Assert.Single(await db.Events.ToListAsync());
    }

    [Fact]
    public async Task The_history_keeps_the_newest_runs_of_a_project_deploys_apart_from_the_rest()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = new DockmasterDb(new DbContextOptionsBuilder<DockmasterDb>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        var n = ProjectService.HistoryKept;
        for (var i = 0; i < n + 5; i++) db.Deployments.Add(new DeploymentEntity { ProjectId = 1, Action = "deploy", ComposeYaml = "services: {}" });
        for (var i = 0; i < n + 20; i++) db.Deployments.Add(new DeploymentEntity { ProjectId = 1, Action = "autoscale" });
        db.Deployments.Add(new DeploymentEntity { ProjectId = 2, Action = "deploy", ComposeYaml = "services: {}" });
        await db.SaveChangesAsync();

        await ProjectService.PruneHistoryAsync(db, 1, default);

        Assert.Equal(n, await db.Deployments.CountAsync(d => d.ProjectId == 1 && d.ComposeYaml != ""));
        Assert.Equal(n, await db.Deployments.CountAsync(d => d.ProjectId == 1 && d.ComposeYaml == ""));
        Assert.Equal(1, await db.Deployments.CountAsync(d => d.ProjectId == 2));
        // the oldest went
        Assert.Equal(6, await db.Deployments.Where(d => d.ProjectId == 1).MinAsync(d => d.Id));
    }
    /// <summary>An installation from before users: its admin password signs in as "admin", the default roles are there once.</summary>
    [Fact]
    public async Task The_old_admin_password_becomes_the_user_admin_and_the_roles_are_made_once()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = new DockmasterDb(new DbContextOptionsBuilder<DockmasterDb>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        db.Settings.Add(new SettingEntity { Key = SettingsService.AdminPasswordKey, Value = Dockmaster.Server.Access.Passwords.Hash("old-password") });
        await db.SaveChangesAsync();

        await db.UpgradeSchemaAsync();
        var admin = await db.Users.SingleAsync();
        Assert.Equal("admin", admin.Name);
        Assert.True(admin.IsAdmin);
        Assert.True(Dockmaster.Server.Access.Passwords.Verify("old-password", admin.PasswordHash));
        Assert.NotEmpty(admin.SessionStamp);
        Assert.Equal(["Developer", "Operator", "Server admin", "Viewer"], (await db.Roles.Select(r => r.Name).ToListAsync()).Order());

        // a role deleted by the admin stays deleted; a second admin is not made
        db.Roles.Remove(await db.Roles.SingleAsync(r => r.Name == "Viewer"));
        await db.SaveChangesAsync();
        await db.UpgradeSchemaAsync();
        Assert.Equal(3, await db.Roles.CountAsync());
        Assert.Equal(1, await db.Users.CountAsync());
        // names ignore case
        Assert.NotNull(await db.Users.FirstOrDefaultAsync(u => u.Name == "ADMIN"));
    }

    [Fact]
    public void A_role_always_has_view_and_its_permissions_in_one_order()
    {
        Assert.Equal(["view", "deploy", "terminal"], Dockmaster.Server.Access.Perm.Parse("terminal, deploy,nonsense"));
        Assert.Equal(["view"], Dockmaster.Server.Access.Perm.Parse(""));
    }

    [Fact]
    public void Access_adds_up_the_all_servers_role_and_the_servers_own()
    {
        var a = new Dockmaster.Server.Access.UserAccess(1, "u", false, new HashSet<string> { "view", "control" },
            new Dictionary<int, IReadOnlySet<string>> { [2] = new HashSet<string> { "view", "terminal" } });
        Assert.True(a.Can(5, "control"));
        Assert.False(a.Can(5, "terminal"));
        Assert.True(a.Can(2, "terminal"));
        Assert.True(a.Can(2, "control"));
        Assert.Equal(["view", "control", "terminal"], a.On(2));
        var none = new Dockmaster.Server.Access.UserAccess(2, "n", false, new HashSet<string>(), new Dictionary<int, IReadOnlySet<string>>());
        Assert.Empty(none.On(2));
        Assert.False(none.Sees(2));
    }
}
