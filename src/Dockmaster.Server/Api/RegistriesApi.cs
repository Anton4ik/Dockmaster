using Microsoft.EntityFrameworkCore;
using Dockmaster.Server.Access;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Server.Api;

public static class RegistriesApi
{
    public static void MapRegistriesApi(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/registries").RequireAuthorization().ForAdmin();

        g.MapGet("/", async (IDbContextFactory<DockmasterDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            return (await db.Registries.OrderBy(r => r.Name).ToListAsync(ct)).Select(r => r.ToDto()).ToArray();
        });

        g.MapPost("/", async (SaveRegistryRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, SecretProtector secrets, CancellationToken ct) =>
        {
            var (name, url) = Normalize(req);
            if (name.Length == 0 || url.Length == 0) return ApiErrors.Bad("name and URL are required");
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Registries.AnyAsync(r => r.Url == url, ct)) return ApiErrors.Conflict("a registry with this URL already exists");
            var e = new RegistryEntity { Name = name, Url = url, Username = req.Username?.Trim() ?? "", Insecure = req.Insecure, PasswordProtected = secrets.Protect(req.Password ?? ""), Aliases = NormalizeAliases(req.Aliases) };
            db.Registries.Add(e);
            await db.SaveChangesAsync(ct);
            await ctx.AuditAsync($"added registry {e.Name} ({e.Url})");
            return Results.Ok(e.ToDto());
        });

        g.MapPut("/{id:int}", async (int id, SaveRegistryRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, SecretProtector secrets, CancellationToken ct) =>
        {
            var (name, url) = Normalize(req);
            if (name.Length == 0 || url.Length == 0) return ApiErrors.Bad("name and URL are required");
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Registries.FindAsync([id], ct) is not { } e) return ApiErrors.NotFound("registry");
            e.Name = name; e.Url = url; e.Username = req.Username?.Trim() ?? ""; e.Insecure = req.Insecure; e.Aliases = NormalizeAliases(req.Aliases);
            if (!string.IsNullOrEmpty(req.Password)) e.PasswordProtected = secrets.Protect(req.Password);
            await db.SaveChangesAsync(ct);
            await ctx.AuditAsync($"saved registry {e.Name}" + (string.IsNullOrEmpty(req.Password) ? "" : ", new password"));
            return Results.Ok(e.ToDto());
        });

        g.MapDelete("/{id:int}", async (int id, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Registries.FindAsync([id], ct) is not { } e) return ApiErrors.NotFound("registry");
            db.Registries.Remove(e);
            await db.SaveChangesAsync(ct);
            await ctx.AuditAsync($"deleted registry {e.Name}");
            return Results.Ok();
        });

        // test with unsaved values (password may be empty → use the stored one for an existing id)
        g.MapPost("/test", async (SaveRegistryRequest req, int? id, IDbContextFactory<DockmasterDb> dbf, RegistryClient client, SecretProtector secrets, CancellationToken ct) =>
        {
            var (_, url) = Normalize(req);
            var password = req.Password ?? "";
            if (password.Length == 0 && id != null)
            {
                await using var db = await dbf.CreateDbContextAsync(ct);
                // the stored password is only ever sent to the address it was saved for (or one of its aliases)
                if (await db.Registries.FindAsync([id.Value], ct) is { } e && e.Serves(url)) password = secrets.Unprotect(e.PasswordProtected);
            }
            var (ok, message) = await client.TestAsync(new RegistryClient.Creds(url, req.Username?.Trim() ?? "", password, req.Insecure, Trusted: true), ct);
            return Results.Ok(new { ok, message });
        });

        g.MapGet("/{id:int}/catalog", async (int id, IDbContextFactory<DockmasterDb> dbf, RegistryClient client, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Registries.FindAsync([id], ct) is not { } e) return ApiErrors.NotFound("registry");
            try { return Results.Ok(await client.CatalogAsync(client.For(e), ct)); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException) { return ApiErrors.Upstream(ex); }
        });

        g.MapGet("/{id:int}/tags", async (int id, string repo, IDbContextFactory<DockmasterDb> dbf, RegistryClient client, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Registries.FindAsync([id], ct) is not { } e) return ApiErrors.NotFound("registry");
            try { return Results.Ok(await client.TagsAsync(client.For(e), repo, ct)); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException) { return ApiErrors.Upstream(ex); }
        });
    }

    private static string NormalizeAliases(string? aliases) =>
        string.Join(",", (aliases ?? "").Split([',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim().TrimEnd('/').Replace("https://", "").Replace("http://", "")).Distinct(StringComparer.OrdinalIgnoreCase));

    private static (string name, string url) Normalize(SaveRegistryRequest req) =>
        ((req.Name ?? "").Trim(), (req.Url ?? "").Trim().TrimEnd('/').Replace("https://", "").Replace("http://", ""));
}
