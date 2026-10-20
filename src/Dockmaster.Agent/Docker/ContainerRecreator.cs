using Dockmaster.Protocol;

namespace Dockmaster.Agent.Docker;

/// <summary>
/// Pull the image of a container that no Dockmaster project manages and re-create the container with the same
/// configuration. The old container is kept, stopped and renamed, until the new one is ready (healthy, or running
/// without a restart for 10 s); if the new one does not get there, the old one comes back. A cancel stops the pull,
/// never the swap itself.
/// </summary>
public sealed class ContainerRecreator
{
    private readonly DockerService _docker;
    private readonly ImagePuller _puller;
    private readonly ImageStore _images;
    private readonly ILogger<ContainerRecreator> _log;

    public ContainerRecreator(DockerService docker, ImagePuller puller, ImageStore images, ILogger<ContainerRecreator> log)
    {
        _docker = docker;
        _puller = puller;
        _images = images;
        _log = log;
    }

    public async Task<(bool ok, string message)> RecreateAsync(RecreateContainer req, Func<string, Task> progress, CancellationToken ct)
    {
        var old = await _docker.InspectAsync(req.ContainerId, ct);
        if (DockerService.IsSelf(old.ID)) return (false, "this is the agent itself — use “update agent” instead");
        var name = old.Name.TrimStart('/');
        var reference = old.Config.Image;

        if (req.Pull)
        {
            if (await _puller.PullAsync(reference, req.Registries, progress, ct) is { } error) return (false, "image pull failed: " + error);
            var image = await _docker.InspectImageAsync(reference, ct);
            if (image.ID == old.Image && !req.Force) return (true, "already running the newest image — nothing to do");
        }

        var oldRaw = await _docker.InspectRawAsync(old.ID, ct) ?? throw new InvalidOperationException($"{name} is gone");
        var oldImage = await _docker.InspectImageRawAsync(old.Image, ct); // removed image: keep everything
        var create = ContainerClone.Parameters(oldRaw, oldImage, name, reference);

        _log.LogInformation("re-creating {Container} from {Image}", name, reference);
        var wasRunning = old.State.Running;
        var renamed = false;
        // From here on the swap runs to the end: a cancel half way would land in the middle of a docker stop (which
        // the daemon finishes anyway) and leave the service down. Only the pull above can be cancelled.
        ct.ThrowIfCancellationRequested();
        var swap = CancellationToken.None;
        try
        {
            if (wasRunning)
            {
                await TryProgress(progress, $"stopping {name}");
                await _docker.StopAsync(old.ID, DockerService.StopTimeoutSeconds, swap);
            }
            await _docker.RenameAsync(old.ID, $"{name}-old", swap);
            renamed = true;
            await TryProgress(progress, "creating the new container");
            var created = await _docker.CreateCloneAsync(name, create, oldRaw, swap);
            if (wasRunning)
            {
                if (!await _docker.StartAsync(created, swap))
                    throw new InvalidOperationException("the new container did not start");
                if (await WaitReadyAsync(created, progress) is { } failure)
                    throw new InvalidOperationException(failure);
            }
            await _docker.RemoveAsync(old.ID, swap);
        }
        catch (Exception ex)
        {
            // never leave the service stopped under a temporary name
            await TryProgress(progress, $"✗ {ex.Message} — restoring the old container");
            await RestoreAsync(old.ID, name, wasRunning, renamed);
            return (false, $"re-create failed, old container restored: {ex.Message}");
        }
        await TryProgress(progress, $"✓ {name} re-created");
        // the swap succeeded: tidying up must not turn that into a failure
        if (req.Pull)
            try { await _images.CleanUpAfterPullAsync(line => TryProgress(progress, line), ct); }
            catch (Exception ex) { _log.LogWarning("image clean-up after re-creating {Container} failed: {Error}", name, ex.Message); }
        return (true, wasRunning ? "re-created and started with the newest image" : "re-created (it was stopped, so it stays stopped)");
    }

    /// <summary>
    /// Null when the new container is ready: healthy (with a healthcheck) or running without a restart for
    /// <see cref="Readiness.Monitor"/> (without) — a container that starts and then crashes must not replace a working one.
    /// </summary>
    private async Task<string?> WaitReadyAsync(string id, Func<string, Task> progress)
    {
        var none = CancellationToken.None;
        var started = DateTime.UtcNow;
        var i = await _docker.InspectAsync(id, none);
        var restarts = i.RestartCount;
        var healthcheck = i.State.Health != null;
        await TryProgress(progress, healthcheck ? "waiting for the healthcheck" : $"watching it for {Readiness.Monitor.TotalSeconds:0}s");
        while (true)
        {
            await Task.Delay(Readiness.Poll, none);
            i = await _docker.InspectAsync(id, none);
            var health = i.State.Health?.Status;
            if (i.RestartCount != restarts || i.State.Status != "running" || health == "unhealthy")
                return $"the new container {(health == "unhealthy" ? "is unhealthy" : $"died (exit code {i.State.ExitCode})")}"
                       + (string.IsNullOrEmpty(i.State.Error) ? "" : $": {i.State.Error}");
            var elapsed = DateTime.UtcNow - started;
            if (healthcheck ? health == "healthy" && elapsed >= Readiness.Settle : elapsed >= Readiness.Monitor) return null;
            if (elapsed > Readiness.Timeout) return $"the new container is still {health ?? i.State.Status} after {Readiness.Timeout.TotalSeconds:0}s";
        }
    }

    private static async Task TryProgress(Func<string, Task> progress, string line)
    {
        try { await progress(line); } catch { } // the connection may be what went away
    }

    private async Task RestoreAsync(string oldId, string name, bool start, bool renamed)
    {
        var none = CancellationToken.None;
        try
        {
            if (renamed)
            {
                await _docker.TryRemoveAsync(name, none); // the half-made new one, if any
                await _docker.RenameAsync(oldId, name, none);
            }
            if (start) await _docker.StartAsync(oldId, none);
        }
        catch (Exception ex) { _log.LogError(ex, "could not restore {Container}", name); }
    }
}
