using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Server.Access;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Server.Api;

/// <summary>/api/users and /api/roles: who signs in, and what each may do on which server (admins only).</summary>
public static partial class UsersApi
{
    private const int MaxNameLength = 64;

    public static void MapUsersApi(this RouteGroupBuilder api)
    {
        MapUsers(api.MapGroup("/users").RequireAuthorization().ForAdmin());
        MapRoles(api.MapGroup("/roles").RequireAuthorization().ForAdmin());
    }

    private static void MapUsers(RouteGroupBuilder g)
    {
        g.MapGet("/", async (IDbContextFactory<DockmasterDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var grants = (await db.UserGrants.ToListAsync(ct)).ToLookup(x => x.UserId);
            return (await db.Users.OrderBy(u => u.Name).ToListAsync(ct)).Select(u => ToDto(u, grants[u.Id])).ToArray();
        });

        g.MapPost("/", async (SaveUserRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, AccessService access, Notifier notifier, CancellationToken ct) =>
            await OneAtATimeAsync(async () =>
        {
            if (req.Password is not { Length: >= Passwords.MinLength } password) return ApiErrors.Bad(Passwords.TooShort);
            await using var db = await dbf.CreateDbContextAsync(ct);
            var u = new UserEntity { PasswordHash = Passwords.Hash(password), SessionStamp = Passwords.NewStamp() };
            if (await ApplyAsync(u, req, db, ct) is { } bad) return bad;
            // the user and their roles together: a failure halfway leaves no user without the roles meant for them
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.Users.Add(u);
            await db.SaveChangesAsync(ct);
            var grants = await SetGrantsAsync(db, u.Id, req.Grants, ct);
            await tx.CommitAsync(ct);
            access.Changed([]);
            await notifier.AuditAsync(ctx.Access(), $"added user {u.Name}" + (u.IsAdmin ? " (admin)" : "") + (await GrantsTextAsync(db, grants, ct) is { Length: > 0 } roles ? ", roles " + roles : ""));
            return Results.Ok(ToDto(u, grants));
        }, ct));

        g.MapPut("/{id:int}", async (int id, SaveUserRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, AccessService access, Notifier notifier,
            CancellationToken ct) => await OneAtATimeAsync(async () =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Users.FindAsync([id], ct) is not { } u) return ApiErrors.NotFound("user");
            var before = (u.Name, u.IsAdmin, u.Disabled, Grants: GrantKeys(await db.UserGrants.Where(x => x.UserId == id).ToListAsync(ct)));
            var me = ctx.Access().UserId == id;
            if (me && (!req.IsAdmin || req.Disabled)) return ApiErrors.Bad("you cannot take admin away from yourself or disable yourself — another admin can");
            // one's own password only where the current one is asked for: a session left open is not enough to take the account
            if (me && !string.IsNullOrEmpty(req.Password)) return ApiErrors.Bad("change your own password in Settings → Account");
            if (u.IsAdmin && !u.Disabled && (!req.IsAdmin || req.Disabled) && !await OtherAdminAsync(db, id, ct))
                return ApiErrors.Conflict("this is the last admin: make someone else admin first");
            var wasDisabled = u.Disabled;
            if (await ApplyAsync(u, req, db, ct) is { } bad) return bad;
            if (!string.IsNullOrEmpty(req.Password))
            {
                if (req.Password.Length < Passwords.MinLength) return ApiErrors.Bad(Passwords.TooShort);
                u.PasswordHash = Passwords.Hash(req.Password);
            }
            // a new password or a disabled account: the sessions signed in before end
            if (!string.IsNullOrEmpty(req.Password) || u.Disabled && !wasDisabled) u.SessionStamp = Passwords.NewStamp();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);
            var grants = await SetGrantsAsync(db, u.Id, req.Grants, ct);
            await tx.CommitAsync(ct);
            access.Changed([id]);
            // what changed, so the event says more than "saved"
            var changes = new List<string>();
            if (before.Name != u.Name) changes.Add($"renamed from {before.Name}");
            if (before.IsAdmin != u.IsAdmin) changes.Add(u.IsAdmin ? "made admin" : "admin taken away");
            if (before.Disabled != u.Disabled) changes.Add(u.Disabled ? "disabled" : "enabled");
            if (!string.IsNullOrEmpty(req.Password)) changes.Add("new password");
            if (before.Grants != GrantKeys(grants)) changes.Add("roles now " + (await GrantsTextAsync(db, grants, ct) is { Length: > 0 } now ? now : "none"));
            await notifier.AuditAsync(ctx.Access(), $"changed user {u.Name}" + (changes.Count > 0 ? ": " + string.Join(", ", changes) : " (nothing different)"));
            return Results.Ok(ToDto(u, grants));
        }, ct));

        g.MapDelete("/{id:int}", async (int id, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, AccessService access, Notifier notifier, CancellationToken ct) =>
            await OneAtATimeAsync(async () =>
        {
            if (ctx.Access().UserId == id) return ApiErrors.Bad("you cannot delete yourself — another admin can");
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Users.FindAsync([id], ct) is not { } u) return ApiErrors.NotFound("user");
            if (u.IsAdmin && !u.Disabled && !await OtherAdminAsync(db, id, ct)) return ApiErrors.Conflict("this is the last admin: make someone else admin first");
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.UserGrants.Where(x => x.UserId == id).ExecuteDeleteAsync(ct);
            await db.Users.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
            access.Changed([id]);
            await notifier.AuditAsync(ctx.Access(), $"deleted user {u.Name}");
            return Results.Ok();
        }, ct));
    }

    private static void MapRoles(RouteGroupBuilder g)
    {
        g.MapGet("/", async (IDbContextFactory<DockmasterDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var users = await UsersByRoleAsync(db, ct);
            return (await db.Roles.OrderBy(r => r.Name).ToListAsync(ct)).Select(r => ToDto(r, users[r.Id])).ToArray();
        });

        // what a role may hold, with what each means: the role form's checkboxes
        g.MapGet("/permissions", () => Perm.All.Select(p => new PermissionDto(p.Key, p.Label, p.Description)).ToArray());

        g.MapPost("/", async (SaveRoleRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, AccessService access, Notifier notifier, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var r = new RoleEntity();
            if (await ApplyAsync(r, req, db, ct) is { } bad) return bad;
            db.Roles.Add(r);
            await db.SaveChangesAsync(ct);
            access.Changed([]);
            await notifier.AuditAsync(ctx.Access(), $"added role {r.Name}: {PermsText(r)}");
            return Results.Ok(ToDto(r, []));
        });

        g.MapPut("/{id:int}", async (int id, SaveRoleRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, AccessService access, Notifier notifier,
            CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Roles.FindAsync([id], ct) is not { } r) return ApiErrors.NotFound("role");
            var was = r.Name;
            if (await ApplyAsync(r, req, db, ct) is { } bad) return bad;
            await db.SaveChangesAsync(ct);
            var holders = await db.UserGrants.Where(x => x.RoleId == id).Select(x => x.UserId).Distinct().ToListAsync(ct);
            access.Changed(holders);
            await notifier.AuditAsync(ctx.Access(), $"changed role {r.Name}{(was != r.Name ? $" (was {was})" : "")}: {PermsText(r)}");
            return Results.Ok(ToDto(r, (await UsersByRoleAsync(db, ct))[id]));
        });

        g.MapDelete("/{id:int}", async (int id, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, AccessService access, Notifier notifier, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Roles.FindAsync([id], ct) is not { } r) return ApiErrors.NotFound("role");
            if ((await UsersByRoleAsync(db, ct))[id].ToList() is { Count: > 0 } users)
                return ApiErrors.Conflict($"{string.Join(", ", users)} {(users.Count == 1 ? "has" : "have")} the role {r.Name} — give them another one first");
            db.Roles.Remove(r);
            await db.SaveChangesAsync(ct);
            access.Changed([]);
            await notifier.AuditAsync(ctx.Access(), $"deleted role {r.Name}");
            return Results.Ok();
        });
    }

    /// <summary>What is wrong with a user name, or null: what one types to sign in, no spaces.</summary>
    public static string? ValidateName(string name) =>
        name.Length is > 0 and <= MaxNameLength && UserNameRegex().IsMatch(name) ? null
            : $"a user name is up to {MaxNameLength} letters, digits, '.', '_', '-' or '@', e.g. anna or anna@example.com";

    private static async Task<IResult?> ApplyAsync(UserEntity u, SaveUserRequest req, DockmasterDb db, CancellationToken ct)
    {
        var name = req.Name?.Trim() ?? "";
        if (ValidateName(name) is { } bad) return ApiErrors.Bad(bad);
        if (await db.Users.AnyAsync(x => x.Name == name && x.Id != u.Id, ct)) return ApiErrors.Conflict($"there is a user {name} already");
        var grants = req.Grants ?? [];
        if (grants.GroupBy(x => x.AgentId).FirstOrDefault(x => x.Count() > 1) is { } twice)
            return ApiErrors.Bad(twice.Key == UserGrantEntity.AllServers ? "one role for all servers" : $"one role per server (server #{twice.Key} has two)");
        var agents = await db.Agents.Select(a => a.Id).ToListAsync(ct);
        if (grants.FirstOrDefault(x => x.AgentId != UserGrantEntity.AllServers && !agents.Contains(x.AgentId)) is { } noServer) return ApiErrors.Bad($"server #{noServer.AgentId} not found");
        var roles = await db.Roles.Select(r => r.Id).ToListAsync(ct);
        if (grants.FirstOrDefault(x => !roles.Contains(x.RoleId)) is { } noRole) return ApiErrors.Bad($"role #{noRole.RoleId} not found");
        u.Name = name;
        u.IsAdmin = req.IsAdmin;
        u.Disabled = req.Disabled;
        return null;
    }

    private static async Task<IResult?> ApplyAsync(RoleEntity r, SaveRoleRequest req, DockmasterDb db, CancellationToken ct)
    {
        var name = req.Name?.Trim() ?? "";
        if (name.Length is 0 or > MaxNameLength) return ApiErrors.Bad($"give the role a name, at most {MaxNameLength} characters");
        if ((req.Permissions ?? []).FirstOrDefault(p => !Perm.Keys.Contains(p)) is { } unknown) return ApiErrors.Bad($"unknown permission '{unknown}'");
        if (await db.Roles.AnyAsync(x => x.Name == name && x.Id != r.Id, ct)) return ApiErrors.Conflict($"there is a role {name} already");
        r.Name = name;
        r.Description = req.Description?.Trim() ?? "";
        r.Permissions = string.Join(",", Perm.Normalize(req.Permissions ?? []));
        return null;
    }

    /// <summary>Replaces the user's grants (null leaves them) and gives back the ones they have now.</summary>
    private static async Task<List<UserGrantEntity>> SetGrantsAsync(DockmasterDb db, int userId, GrantDto[]? grants, CancellationToken ct)
    {
        if (grants != null)
        {
            await db.UserGrants.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
            db.UserGrants.AddRange(grants.Select(x => new UserGrantEntity { UserId = userId, AgentId = x.AgentId, RoleId = x.RoleId }));
            await db.SaveChangesAsync(ct);
        }
        return await db.UserGrants.Where(x => x.UserId == userId).ToListAsync(ct);
    }

    // one change of users at a time: "is there another admin" and the change that relies on it come without another
    // in between, or two admins taking admin away from each other at once would leave none
    private static readonly SemaphoreSlim Changing = new(1, 1);

    private static async Task<IResult> OneAtATimeAsync(Func<Task<IResult>> change, CancellationToken ct)
    {
        await Changing.WaitAsync(ct);
        try { return await change(); }
        finally { Changing.Release(); }
    }

    private static Task<bool> OtherAdminAsync(DockmasterDb db, int userId, CancellationToken ct) => db.Users.AnyAsync(u => u.IsAdmin && !u.Disabled && u.Id != userId, ct);

    /// <summary>The grants as one comparable string: whether they changed.</summary>
    private static string GrantKeys(IEnumerable<UserGrantEntity> grants) => string.Join(",", grants.Select(x => $"{x.AgentId}:{x.RoleId}").Order(StringComparer.Ordinal));

    /// <summary>"operator on nas, viewer on all servers" for an event; "" without grants.</summary>
    private static async Task<string> GrantsTextAsync(DockmasterDb db, IReadOnlyCollection<UserGrantEntity> grants, CancellationToken ct)
    {
        if (grants.Count == 0) return "";
        var roles = await db.Roles.ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var agents = await db.AgentNamesAsync(ct);
        return string.Join(", ", grants.OrderBy(x => x.AgentId).Select(x =>
            $"{roles.GetValueOrDefault(x.RoleId, $"#{x.RoleId}")} on {(x.AgentId == UserGrantEntity.AllServers ? "all servers" : agents.GetValueOrDefault(x.AgentId, $"server #{x.AgentId}"))}"));
    }

    private static string PermsText(RoleEntity r) => r.PermissionList.Count > 0 ? string.Join(", ", r.PermissionList.Select(AccessService.Label)) : "no permissions";

    private static async Task<ILookup<int, string>> UsersByRoleAsync(DockmasterDb db, CancellationToken ct)
    {
        var names = await db.Users.Select(u => new { u.Id, u.Name }).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        return (await db.UserGrants.ToListAsync(ct)).Where(x => names.ContainsKey(x.UserId)).Select(x => (x.RoleId, Name: names[x.UserId])).Distinct()
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToLookup(x => x.RoleId, x => x.Name);
    }

    private static UserDto ToDto(UserEntity u, IEnumerable<UserGrantEntity> grants) => new(u.Id, u.Name, u.IsAdmin, u.Disabled,
        [.. grants.OrderBy(x => x.AgentId).Select(x => new GrantDto(x.AgentId, x.RoleId))], Mappers.Utc(u.CreatedAt), Mappers.Utc(u.LastLoginAt));

    private static RoleDto ToDto(RoleEntity r, IEnumerable<string> users) => new(r.Id, r.Name, r.Description, [.. r.PermissionList], [.. users]);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._@-]*$")] private static partial Regex UserNameRegex();
}
