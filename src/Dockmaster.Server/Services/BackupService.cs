using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Server.Data;

namespace Dockmaster.Server.Services;

/// <summary>
/// Backups of the server's own data: the database (projects, secrets, history), the agents' TLS certificate (agents
/// pin it) and the data-protection keys (without them stored secrets and passwords cannot be read). A zip on demand,
/// one a day in &lt;data&gt;/backups (the last 7), and a restore that is put in place on the next start.
/// </summary>
public sealed class BackupService : BackgroundService
{
    public const string BackupsDir = "backups";
    private const string RestoreDir = "restore-pending";
    // written last into a prepared restore: without it the restore is incomplete
    private const string ReadyMarker = "READY";
    private const int Keep = 7;
    private const int KeepBeforeRestore = 3;
    private static readonly string[] Files = ["agent-tls.pfx", "metrics.json.gz"];

    private readonly string _dataDir;
    private readonly IDbContextFactory<DockmasterDb> _db;
    private readonly ILogger<BackupService> _log;
    // one backup or restore at a time
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BackupService(IConfiguration config, IWebHostEnvironment env, IDbContextFactory<DockmasterDb> db, ILogger<BackupService> log)
    {
        _dataDir = config["DataDir"] ?? Path.Combine(env.ContentRootPath, "data");
        _db = db;
        _log = log;
    }

    public sealed record BackupFile(string Name, long Size, DateTime At);

    /// <summary>A zip of everything the server needs to come back as it was.</summary>
    public async Task WriteZipAsync(Stream output, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dockmaster-{Guid.NewGuid():N}.db");
        try
        {
            // a consistent copy while the server keeps writing
            await using (var db = await _db.CreateDbContextAsync(ct))
#pragma warning disable EF1002 // the path is ours, not input
                await db.Database.ExecuteSqlRawAsync($"VACUUM INTO '{tmp.Replace("'", "''")}'", ct);
#pragma warning restore EF1002
            using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            zip.CreateEntryFromFile(tmp, "dockmaster.db");
            foreach (var f in Files.Where(f => File.Exists(Path.Combine(_dataDir, f))))
                zip.CreateEntryFromFile(Path.Combine(_dataDir, f), f);
            var keys = Path.Combine(_dataDir, "keys");
            if (Directory.Exists(keys))
                foreach (var k in Directory.GetFiles(keys)) zip.CreateEntryFromFile(k, "keys/" + Path.GetFileName(k));
            await using var readme = new StreamWriter(zip.CreateEntry("RESTORE.txt").Open());
            await readme.WriteAsync($"""
                Dockmaster server backup, {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC.
                Restore: Settings -> Backup -> restore (upload this zip), or by hand: stop the server, put these files into its
                data volume (/data), remove dockmaster.db-wal and dockmaster.db-shm there, start it again.
                keys/ decrypts the stored secrets and registry passwords; agent-tls.pfx is the certificate the agents pin.
                """);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    public IReadOnlyList<BackupFile> List()
    {
        var dir = Path.Combine(_dataDir, BackupsDir);
        if (!Directory.Exists(dir)) return [];
        return new DirectoryInfo(dir).GetFiles("dockmaster-*.zip").OrderByDescending(f => f.Name).Select(f => new BackupFile(f.Name, f.Length, f.LastWriteTimeUtc)).ToList();
    }

    /// <summary>One of the daily backups, or null for a name that is not one.</summary>
    public string? PathOf(string name) =>
        List().Any(b => b.Name == name) ? Path.Combine(_dataDir, BackupsDir, name) : null;

    /// <summary>
    /// A backup into &lt;data&gt;/backups; the daily ones and those made on demand share the rotation (the last 7). Written
    /// as .part and renamed when complete: a failed one never counts.
    /// </summary>
    public async Task<BackupFile> CreateDailyAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var dir = Path.Combine(_dataDir, BackupsDir);
            Directory.CreateDirectory(dir);
            // left by a crash
            foreach (var part in Directory.GetFiles(dir, "*.part")) File.Delete(part);
            var path = Path.Combine(dir, $"dockmaster-{DateTime.UtcNow:yyyy-MM-dd-HHmm}.zip");
            try
            {
                await using (var fs = File.Create(path + ".part")) await WriteZipAsync(fs, ct);
                File.Move(path + ".part", path, true);
            }
            finally { File.Delete(path + ".part"); }
            foreach (var old in new DirectoryInfo(dir).GetFiles("dockmaster-*.zip").OrderByDescending(f => f.Name).Skip(Keep)) old.Delete();
            var info = new FileInfo(path);
            return new BackupFile(info.Name, info.Length, info.LastWriteTimeUtc);
        }
        finally { _gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // one a day: none yet today (UTC) → make it
                    if (!List().Any(b => b.At.Date == DateTime.UtcNow.Date))
                        _log.LogInformation("daily backup {Name}", (await CreateDailyAsync(stoppingToken)).Name);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "daily backup failed"); }
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Checks an uploaded backup and puts it aside; <see cref="ApplyPendingRestore"/> moves it in place on the next
    /// start (the database cannot be swapped under a running server). The error when it is no Dockmaster backup.
    /// Prepared in a folder of its own, marked ready last, then renamed: a restore cut halfway is never applied.
    /// </summary>
    public async Task<string?> StageRestoreAsync(Stream upload, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var tmp = Path.Combine(_dataDir, $"{RestoreDir}.tmp-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tmp);
            var zipPath = Path.Combine(tmp, "upload.zip");
            await using (var fs = File.Create(zipPath)) await upload.CopyToAsync(fs, ct);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                if (zip.GetEntry("dockmaster.db") == null) return "this zip has no dockmaster.db — it is not a Dockmaster backup";
                // without them the agents reject the server and the stored secrets cannot be read
                if (zip.GetEntry("agent-tls.pfx") == null || !zip.Entries.Any(e => e.FullName.StartsWith("keys/") && e.Name.Length > 0))
                    return "this backup has no agent-tls.pfx or no keys/ — restoring it would cut off the agents and lose the secrets";
                var entries = zip.Entries.Where(e => e.FullName == "dockmaster.db" || Files.Contains(e.FullName) || e.FullName.StartsWith("keys/") && !e.FullName.Contains("..")).ToList();
                // a zip bomb, or simply no room for it
                var size = entries.Sum(e => e.Length);
                var free = new DriveInfo(Path.GetFullPath(_dataDir)).AvailableFreeSpace;
                if (size > free / 2) return $"not enough free space: the backup unpacks to {size / (1024 * 1024)} MB, the data folder has {free / (1024 * 1024)} MB free";
                foreach (var e in entries)
                {
                    var target = Path.Combine(tmp, e.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (e.Name.Length > 0) e.ExtractToFile(target, true);
                }
            }
            File.Delete(zipPath);
            // a database that opens and has projects in it
            await using (var conn = new SqliteConnection($"Data Source={Path.Combine(tmp, "dockmaster.db")};Mode=ReadOnly;Pooling=False"))
            {
                await conn.OpenAsync(ct);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM \"Projects\"";
                await cmd.ExecuteScalarAsync(ct);
            }
            await File.WriteAllTextAsync(Path.Combine(tmp, ReadyMarker), DateTime.UtcNow.ToString("O"), ct);
            var staging = Path.Combine(_dataDir, RestoreDir);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.Move(tmp, staging);
            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or SqliteException)
        {
            return "the backup cannot be read: " + ex.Message;
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
            catch (Exception ex) { _log.LogWarning("could not remove {Dir}: {Error}", tmp, ex.Message); }
            _gate.Release();
        }
    }

    /// <summary>
    /// Before anything opens the data: a restore put aside by the last run replaces the files it has, the old ones are
    /// kept in before-restore-&lt;time&gt;/ (the last 3). One not marked ready is dropped. Static: it runs before the
    /// services are built.
    /// </summary>
    public static void ApplyPendingRestore(string dataDir, Action<string> log)
    {
        // left by a restore cut short while it was prepared
        foreach (var d in Directory.GetDirectories(dataDir, RestoreDir + ".tmp-*"))
        {
            try { Directory.Delete(d, true); }
            catch (Exception ex) { log($"could not remove {d}: {ex.Message}"); }
        }
        var staging = Path.Combine(dataDir, RestoreDir);
        if (!Directory.Exists(staging)) return;
        if (!File.Exists(Path.Combine(staging, ReadyMarker)))
        {
            Directory.Delete(staging, true);
            log("dropped a restore that was not completely prepared");
            return;
        }
        var aside = Path.Combine(dataDir, $"before-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(aside);
        // only what the backup replaces
        var replaced = Files.Where(f => File.Exists(Path.Combine(staging, f))).ToList();
        if (File.Exists(Path.Combine(staging, "dockmaster.db"))) replaced.AddRange(["dockmaster.db", "dockmaster.db-wal", "dockmaster.db-shm"]);
        foreach (var f in replaced)
            if (File.Exists(Path.Combine(dataDir, f))) File.Move(Path.Combine(dataDir, f), Path.Combine(aside, f));
        var stagedKeys = Path.Combine(staging, "keys");
        if (Directory.Exists(stagedKeys) && Directory.Exists(Path.Combine(dataDir, "keys"))) Directory.Move(Path.Combine(dataDir, "keys"), Path.Combine(aside, "keys"));
        foreach (var f in Directory.GetFiles(staging).Where(f => Path.GetFileName(f) != ReadyMarker)) File.Move(f, Path.Combine(dataDir, Path.GetFileName(f)));
        if (Directory.Exists(stagedKeys)) Directory.Move(stagedKeys, Path.Combine(dataDir, "keys"));
        // the marker goes last: a start cut short in between puts the rest in place the next time
        Directory.Delete(staging, true);
        log($"restored the backup; the data before it is in {aside}");
        foreach (var old in new DirectoryInfo(dataDir).GetDirectories("before-restore-*").OrderByDescending(d => d.Name).Skip(KeepBeforeRestore))
        {
            try { old.Delete(true); }
            catch (Exception ex) { log($"could not remove {old.FullName}: {ex.Message}"); }
        }
    }
}
