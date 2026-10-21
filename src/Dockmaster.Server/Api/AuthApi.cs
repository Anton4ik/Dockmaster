using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Server.Access;
using Dockmaster.Server.Data;

namespace Dockmaster.Server.Api;

/// <summary>Signing in and out, the first admin on the first run, and one's own password.</summary>
public static class AuthApi
{
    private static readonly TimeSpan WrongPasswordDelay = TimeSpan.FromMilliseconds(800);
    /// <summary>The name sign-ins without one use: the one user there was before users existed.</summary>
    public const string DefaultAdmin = "admin";
    /// <summary>The rate limiter policy on signing in and on the first run: requests per address.</summary>
    public const string RateLimit = "auth";

    public static void MapAuthApi(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth");

        g.MapGet("/me", async (HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var a = ctx.AccessOrNull();
            return new MeDto(a != null, await db.Users.AnyAsync(ct), a == null ? null : Me(a));
        }).Public();

        // the first run: whoever has the setup code from the server's log makes the first admin (DOCKMASTER_ADMINPASSWORD
        // does it without the page)
        g.MapPost("/setup", async (SetupRequest req, IDbContextFactory<DockmasterDb> dbf, AccessService access, SetupCode code, CancellationToken ct) =>
        {
            var name = req.Name?.Trim() is { Length: > 0 } n ? n : DefaultAdmin;
            if (UsersApi.ValidateName(name) is { } badName) return ApiErrors.Bad(badName);
            if (req.Password is not { Length: >= Passwords.MinLength } password) return ApiErrors.Bad(Passwords.TooShort);
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Users.AnyAsync(ct)) return ApiErrors.Conflict("Dockmaster is set up already — sign in");
            if (!code.Matches(req.Code))
            {
                await Task.Delay(WrongPasswordDelay, ct);
                return Results.Json(new { error = "wrong setup code: the server prints it to its log at start (docker logs dockmaster)" }, statusCode: StatusCodes.Status403Forbidden);
            }
            db.Users.Add(new UserEntity { Name = name, PasswordHash = Passwords.Hash(password), IsAdmin = true, SessionStamp = Passwords.NewStamp() });
            await db.SaveChangesAsync(ct);
            access.Changed([]);
            return Results.Ok();
        }).Public().RequireRateLimiting(RateLimit);

        g.MapPost("/login", async (LoginRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, LoginThrottle throttle, CancellationToken ct) =>
        {
            var name = req.Name?.Trim() is { Length: > 0 } n ? n : DefaultAdmin;
            if (throttle.Blocked(ctx, name))
                return Results.Json(new { error = "too many wrong passwords for this name — try again in a few minutes" }, statusCode: StatusCodes.Status429TooManyRequests);
            await using var db = await dbf.CreateDbContextAsync(ct);
            var user = await db.Users.FirstOrDefaultAsync(u => u.Name == name, ct);
            // an unknown name costs the same time as a wrong password: nothing to learn about who exists
            if (!Passwords.Verify(req.Password ?? "", user?.PasswordHash))
            {
                throttle.Failed(ctx, name);
                await Task.Delay(WrongPasswordDelay, ct);
                return Results.Json(new { error = "wrong name or password" }, statusCode: StatusCodes.Status401Unauthorized);
            }
            if (user!.Disabled) return Results.Json(new { error = "this account is disabled — ask an admin" }, statusCode: StatusCodes.Status401Unauthorized);
            user.LastLoginAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await SignInAsync(ctx, user);
            return Results.Ok();
        }).Public().RequireRateLimiting(RateLimit);

        g.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        }).Public();

        // one's own password; the other sessions of the user end, this one goes on
        g.MapPost("/password", async (PasswordRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, AccessService access, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Users.FindAsync([ctx.Access().UserId], ct) is not { } user) return ApiErrors.NotFound("user");
            if (!Passwords.Verify(req.Current ?? "", user.PasswordHash)) return ApiErrors.Bad("current password is wrong");
            if (req.Password is not { Length: >= Passwords.MinLength } password) return ApiErrors.Bad(Passwords.TooShort);
            user.PasswordHash = Passwords.Hash(password);
            user.SessionStamp = Passwords.NewStamp();
            await db.SaveChangesAsync(ct);
            access.Changed([user.Id]);
            await SignInAsync(ctx, user);
            return Results.Ok();
        }).ForAnyone();
    }

    public static Task SignInAsync(HttpContext ctx, UserEntity user)
    {
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(AccessService.StampClaim, user.SessionStamp),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        return ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
    }

    public static MeUserDto Me(UserAccess a) => new(a.UserId, a.Name, a.IsAdmin, a.Everywhere.Count > 0 ? [.. Perm.Normalize(a.Everywhere)] : [],
        a.ByServer.ToDictionary(x => x.Key, x => Perm.Normalize(x.Value).ToArray()));
}
