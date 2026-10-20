using Dockmaster.Protocol;
using Dockmaster.Server.Access;
using Dockmaster.Server.Services;

namespace Dockmaster.Server.Api;

/// <summary>About the server itself: its version, and the image update check on demand.</summary>
public static class SystemApi
{
    // a backup is the database and a few small files: far below this
    private const long MaxRestoreUpload = 2L * 1024 * 1024 * 1024;

    public static void MapSystemApi(this RouteGroupBuilder api)
    {
        api.MapGet("/version", () => new { version = BuildInfo.Version(typeof(Program).Assembly) }).RequireAuthorization().ForAnyone();

        // asks the registries about every image now (it happens by itself every few hours); changes nothing
        api.MapGroup("/updates").RequireAuthorization().MapPost("/check", async (ImageUpdateChecker updates, CancellationToken ct) =>
            new { checkedImages = await updates.CheckAllAsync(ct), at = updates.LastCheck }).ForAnyone();

        // backups of the server's own data (database, agent certificate, keys)
        var b = api.MapGroup("/system").RequireAuthorization().ForAdmin();
        b.MapGet("/backup", async (BackupService backups, CancellationToken ct) =>
        {
            // zip writes synchronously, which a response stream refuses: into a temporary file first
            var tmp = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
            try { await backups.WriteZipAsync(tmp, ct); }
            catch
            {
                await tmp.DisposeAsync();
                throw;
            }
            tmp.Position = 0;
            return Results.File(tmp, "application/zip", $"dockmaster-{DateTime.UtcNow:yyyy-MM-dd-HHmm}.zip");
        });
        b.MapGet("/backups", (BackupService backups) => backups.List());
        b.MapPost("/backups", async (BackupService backups, CancellationToken ct) => await backups.CreateDailyAsync(ct));
        b.MapGet("/backups/{name}", (string name, BackupService backups) =>
            backups.PathOf(name) is { } path ? Results.File(path, "application/zip", name) : ApiErrors.NotFound("backup"));
        // the zip as the request body; put in place on the next start, and the server stops for docker to start it again
        b.MapPost("/restore", async (HttpContext ctx, BackupService backups, IHostApplicationLifetime life, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (await backups.StageRestoreAsync(ctx.Request.Body, ct) is { } error) return ApiErrors.Bad(error);
            logs.CreateLogger("Dockmaster.Backup").LogWarning("a backup was uploaded for restore: stopping, docker starts the server again with it");
            _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => life.StopApplication());
            return Results.Ok(new { message = "restoring — the server restarts with the backup in a few seconds" });
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxRestoreUpload));
    }
}
