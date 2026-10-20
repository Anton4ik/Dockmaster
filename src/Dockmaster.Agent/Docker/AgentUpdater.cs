using System.Net;
using System.Text.Json.Nodes;

namespace Dockmaster.Agent.Docker;

/// <summary>
/// Replaces the agent's own container with one running the newest image of the same reference:
/// 1. pull (the agent's logins, then the host's own docker login through a helper container);
/// 2. create "&lt;name&gt;-next" through the API as a copy of the running container with the new image;
/// 3. a detached helper swaps the names, stops the old agent and starts the new one — and puts the old one back if
///    the new one does not start. The agent answers the server before step 3 kills it.
/// </summary>
public sealed class AgentUpdater
{
    private readonly DockerService _docker;
    private readonly ComposeRunner _compose;
    private readonly ImagePuller _puller;
    private readonly ProjectLocks _locks;
    private readonly ILogger<AgentUpdater> _log;

    public AgentUpdater(DockerService docker, ComposeRunner compose, ImagePuller puller, ProjectLocks locks, ILogger<AgentUpdater> log)
    {
        _locks = locks;
        _docker = docker;
        _compose = compose;
        _puller = puller;
        _log = log;
    }

    public async Task<string> UpdateAsync(Func<string, Task> progress, CancellationToken ct)
    {
        if (!DockerService.InContainer) throw new InvalidOperationException("the agent does not run in a container — replace its binary by hand");
        var self = await _docker.InspectAsync(DockerService.SelfId, ct);
        var name = self.Name.TrimStart('/');
        var reference = self.Config.Image;

        if (await _puller.PullAsync(reference, [], progress, ct) is { } error) throw new InvalidOperationException("pull failed: " + error);
        var image = await _docker.InspectImageAsync(reference, ct);
        var selfRaw = await _docker.InspectRawAsync(self.ID, ct) ?? throw new InvalidOperationException("the agent's own container is gone");
        // the newest image already, but started without a mount it needs now (the update before was made by an agent
        // that did not know it): the container is made again all the same
        var create = ContainerClone.Parameters(selfRaw, await _docker.InspectImageRawAsync(self.Image, ct), name + "-next", reference);
        if (!AddHostMounts(create) && image.ID == self.Image) return "already running the newest image";

        var next = name + "-next";
        await RemoveIfThereAsync(next, ct);
        await progress("creating the new agent container");
        await _docker.CreateCloneAsync(next, create, selfRaw, ct);

        var script = string.Join('\n',
            "sleep 3",
            $"docker rm -f {name}-old 2>/dev/null",
            $"docker rename {name} {name}-old",
            $"docker rename {next} {name}",
            $"docker stop -t 15 {name}-old",
            // on success the previous agent image is left untagged and unused: take it away too
            $"if docker start {name}; then docker rm {name}-old; docker rmi {self.Image} 2>/dev/null; else docker rm -f {name}; docker rename {name}-old {name}; docker start {name}; fi");
        // the helper runs from the new image, so the old one is free to go once the swap worked
        string[] swap = ["run", "-d", "--rm", "--name", $"{name}-updater", "--entrypoint", "sh", "-v", $"{_compose.HostSocketPath}:/var/run/docker.sock", image.ID, "-c", script];
        await RemoveIfThereAsync(name + "-updater", ct);
        // the swap stops this agent: a deploy going on would be cut off half way, so the helper starts only when none
        // runs (the few seconds until it stops the agent are left open: the answer to the server must get out first)
        await progress("waiting for deploys on this server to finish");
        var (code, _, err) = await _locks.ExclusiveAsync(() => _compose.CaptureAsync("docker", swap, ct), ct);
        if (code != 0)
        {
            await RemoveIfThereAsync(next, ct);
            throw new InvalidOperationException($"could not start the swap helper: {err.Trim()}");
        }
        var shortId = image.ID.Replace("sha256:", "")[..12];
        _log.LogInformation("updating to image {Image}, restarting in a few seconds", shortId);
        return $"new image {shortId} — the agent restarts now";
    }

    /// <summary>
    /// What an agent started before they were in its <c>docker run</c> gets on its next update: today the host's
    /// cgroup tree, read-only, for the containers' memory peaks (<see cref="CgroupPeaks"/>). False when it had them.
    /// </summary>
    public static bool AddHostMounts(JsonObject create)
    {
        if (create["HostConfig"] is not JsonObject host) create["HostConfig"] = host = [];
        var binds = (host["Binds"] as JsonArray ?? []).Select(b => b?.GetValue<string>() ?? "");
        var mounts = (host["Mounts"] as JsonArray ?? []).Select(m => m?["Target"]?.GetValue<string>() ?? "");
        if (binds.Any(b => b.Split(':') is [_, CgroupPeaks.HostMount, ..]) || mounts.Contains(CgroupPeaks.HostMount)) return false;
        if (host["Binds"] is not JsonArray list) host["Binds"] = list = [];
        list.Add($"/sys/fs/cgroup:{CgroupPeaks.HostMount}:ro");
        return true;
    }

    private async Task RemoveIfThereAsync(string name, CancellationToken ct)
    {
        try { await _docker.RemoveAsync(name, ct); }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }
    }
}
