using System.Net;
using System.Text.Json;

namespace Dockmaster.Agent.Docker;

/// <summary>Images on this host: what is there, what uses it, and removing what nothing needs.</summary>
public sealed class ImageStore
{
    private readonly EngineApi _api;

    public ImageStore(DockerService docker) => _api = docker.Api;

    public sealed record ImageEntry(string Id, string[] Tags, long Size, long Created, string[] Containers, bool Dangling);
    public sealed record RemoveOutcome(string[] Removed, Dictionary<string, string> Failed, long Freed);

    /// <summary>All top-level images with the names of the containers (running or stopped) created from each.</summary>
    private async Task<List<ImageEntry>> ListAsync(CancellationToken ct)
    {
        var images = await _api.ListImagesAsync(ct);
        var users = (await _api.ListContainersAsync(true, ct))
            .GroupBy(c => c.ImageID)
            .ToDictionary(g => g.Key, g => g.Select(c => c.DisplayName()).Order().ToArray());
        return images
            .Select(i =>
            {
                var tags = (i.RepoTags ?? []).Where(t => t != "<none>:<none>").ToArray();
                return new ImageEntry(i.ID, tags, i.Size, i.Created,
                    users.GetValueOrDefault(i.ID, []), tags.Length == 0);
            })
            .OrderByDescending(i => i.Size)
            .ToList();
    }

    public async Task<string> ListJsonAsync(CancellationToken ct) => JsonSerializer.Serialize(await ListAsync(ct), AgentJson.Web);

    /// <summary>
    /// Remove the given images. Images a container uses are refused (removing them would need force and break the
    /// container's next start); a tagged image is removed tag by tag, so no force is needed for several tags either.
    /// </summary>
    private async Task<RemoveOutcome> RemoveAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var byId = (await ListAsync(ct)).ToDictionary(i => i.Id);
        var removed = new List<string>();
        var failed = new Dictionary<string, string>();
        long freed = 0;
        foreach (var id in ids.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            if (!byId.TryGetValue(id, out var image)) { failed[id] = "no such image"; continue; }
            if (image.Containers.Length > 0) { failed[id] = "used by " + string.Join(", ", image.Containers); continue; }
            try
            {
                foreach (var tag in image.Tags) await _api.DeleteImageAsync(tag, ct);
                if (image.Tags.Length == 0) await _api.DeleteImageAsync(id, ct);
                removed.Add(id);
                freed += image.Size;
            }
            catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                removed.Add(id); // the last tag already took it away
                freed += image.Size;
            }
            catch (DockerApiException ex) { failed[id] = ex.Message; }
        }
        return new RemoveOutcome([.. removed], failed, freed);
    }

    public async Task<string> RemoveJsonAsync(IEnumerable<string> ids, CancellationToken ct) => JsonSerializer.Serialize(await RemoveAsync(ids, ct), AgentJson.Web);

    /// <summary>
    /// Remove images no container (running or stopped) uses. Default: only dangling ones, the untagged leftovers a
    /// pull of a newer version leaves behind. allUnused also removes tagged images nothing runs from.
    /// </summary>
    public async Task<string> PruneAsync(bool allUnused, CancellationToken ct)
    {
        var r = await _api.PruneImagesAsync(allUnused, ct);
        var removed = r.ImagesDeleted?.Count(d => !string.IsNullOrEmpty(d.Deleted)) ?? 0;
        return removed == 0 ? "no unused images" : $"freed {HumanBytes(r.SpaceReclaimed)}";
    }

    /// <summary>Clean-up after a deploy pulled newer images; never fails the deploy.</summary>
    public async Task CleanUpAfterPullAsync(Func<string, Task> log, CancellationToken ct)
    {
        try { await log("clean-up: " + await PruneAsync(false, ct)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { await log($"clean-up skipped: {ex.Message}"); }
    }

    private static string HumanBytes(ulong b)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = b;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {units[i]}";
    }
}
