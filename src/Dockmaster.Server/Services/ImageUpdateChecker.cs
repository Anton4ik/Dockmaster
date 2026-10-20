using System.Collections.Concurrent;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Protocol;
using Dockmaster.Server.Data;

namespace Dockmaster.Server.Services;

/// <summary>
/// Compares the image every container runs with what its registry serves for the same tag now.
/// Checks every 15 minutes (HEAD on the manifest, which Docker Hub does not count as a pull) or on demand.
/// </summary>
public sealed class ImageUpdateChecker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    // gives agents a moment to send their first heartbeat after a restart
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RecheckTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(20);
    private const int ParallelChecks = 4;

    /// <summary>What the registry said of one image reference. The texts are made once: every push of a container list reads them.</summary>
    private sealed record Entry(string? Digest, string? Error, DateTime CheckedAt)
    {
        public string LatestDetail { get; } = $"matches the registry (checked {CheckedAt:u})";
        public string OutdatedDetail { get; } = $"the registry has a newer image for this tag (checked {CheckedAt:u})";
    }

    private readonly AgentRegistry _agents;
    private readonly RegistryClient _client;
    private readonly IDbContextFactory<DockmasterDb> _dbf;
    private readonly ILogger<ImageUpdateChecker> _log;
    private readonly ConcurrentDictionary<string, Entry> _cache = new();
    private readonly SemaphoreSlim _running = new(1, 1);

    public ImageUpdateChecker(AgentRegistry agents, RegistryClient client, IDbContextFactory<DockmasterDb> dbf, ILogger<ImageUpdateChecker> log)
    {
        _agents = agents;
        _client = client;
        _dbf = dbf;
        _log = log;
    }

    public DateTime? LastCheck { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartDelay, stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CheckAllAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "image update check failed"); }
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Checks these images again right away, e.g. after a deploy pulled them: the registry may have a build newer
    /// than the last periodic check, and the dot should say so now rather than in 15 minutes.
    /// </summary>
    private Task<int> RecheckAsync(IEnumerable<string> images, CancellationToken ct) =>
        CheckAsync(images.Where(r => ImageRef.Parse(r) != null).Distinct().ToList(), ct);

    /// <summary>Re-checks after a deploy without holding up its answer, then tells the UI.</summary>
    public void RecheckInBackground(int agentId, IEnumerable<string> images)
    {
        var list = images.ToList();
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(RecheckTimeout);
                await RecheckAsync(list, cts.Token);
                _agents.Notify(agentId);
            }
            catch (Exception ex) { _log.LogDebug(ex, "re-check after deploy failed"); }
        });
    }

    /// <summary>Checks every image the agents run now (waits for a check already running); returns how many were checked.</summary>
    public Task<int> CheckAllAsync(CancellationToken ct) =>
        CheckAsync(_agents.All.SelectMany(s => s.Last?.Containers ?? []).Select(c => c.ImageRef).Where(r => ImageRef.Parse(r) != null).Distinct().ToList(), ct);

    private async Task<int> CheckAsync(List<string> refs, CancellationToken ct)
    {
        if (refs.Count == 0) return 0;
        await _running.WaitAsync(ct);
        try
        {
            List<RegistryEntity> regs;
            await using (var db = await _dbf.CreateDbContextAsync(ct)) regs = await db.Registries.ToListAsync(ct);

            await Parallel.ForEachAsync(refs, new ParallelOptions { MaxDegreeOfParallelism = ParallelChecks, CancellationToken = ct }, async (r, token) =>
            {
                var (host, repo, tag) = ImageRef.Parse(r)!.Value;
                var reg = ImageRef.RegistryFor(regs, host);
                // aliases are queried through the registry's main address: same images, and that is the address known to work
                var creds = reg != null ? _client.For(reg) with { Url = reg.Serves(host) && reg.Url != host ? reg.Url : host } : new RegistryClient.Creds(host, "", "", false);
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    cts.CancelAfter(ManifestTimeout);
                    _cache[r] = new Entry(await _client.ManifestDigestAsync(creds, repo, tag, cts.Token), null, DateTime.UtcNow);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    var detail = (ex as HttpRequestException)?.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => reg == null
                            ? $"no access to {host}: add it under Registries (or the image is a local build)"
                            : $"the stored credentials for {host} were rejected",
                        HttpStatusCode.NotFound => "tag not found in the registry",
                        _ => ex.Message,
                    };
                    _cache[r] = new Entry(null, detail, DateTime.UtcNow);
                }
            });
            LastCheck = DateTime.UtcNow;
            _log.LogInformation("checked {n} images for updates", refs.Count);
            return refs.Count;
        }
        finally { _running.Release(); }
    }

    /// <summary>"latest", "outdated", "redeploy" (newer image already pulled), "pinned", "unknown" or "" (no reference).</summary>
    public (string Status, string? Detail) Status(ContainerInfo c)
    {
        if (string.IsNullOrEmpty(c.ImageRef) || c.ImageRef.StartsWith("sha256:")) return ("", null);
        if (c.ImageRef.Contains('@')) return ("pinned", "image is pinned by digest");
        if (c.NewerImageLocal) return ("redeploy", "a newer image for this tag is already pulled on the host; deploy to use it");
        if (!_cache.TryGetValue(c.ImageRef, out var e)) return ("unknown", "not checked yet");
        if (e.Digest == null) return ("unknown", e.Error);
        foreach (var d in c.RepoDigests)
            if (d.Length > e.Digest.Length && d[^(e.Digest.Length + 1)] == '@' && d.EndsWith(e.Digest, StringComparison.Ordinal)) return ("latest", e.LatestDetail);
        return ("outdated", e.OutdatedDetail);
    }
}
