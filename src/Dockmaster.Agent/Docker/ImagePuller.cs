using Microsoft.Extensions.Options;
using Dockmaster.Protocol;

namespace Dockmaster.Agent.Docker;

/// <summary>
/// Pulls one image by reference. First with the logins the server sent (or whatever the agent is logged in to), then,
/// if that fails, through a helper container that sees the host's own `docker login` (/root/.docker): hosts where
/// someone ran `docker login` by hand pull private images that Dockmaster has no stored login for.
/// </summary>
public sealed class ImagePuller
{
    private readonly ComposeRunner _compose;
    private readonly DockerService _docker;
    private readonly AgentOptions _opts;

    public ImagePuller(ComposeRunner compose, DockerService docker, IOptions<AgentOptions> opts)
    {
        _compose = compose;
        _docker = docker;
        _opts = opts.Value;
    }

    /// <summary>Returns null on success, otherwise the error text.</summary>
    public async Task<string?> PullAsync(string reference, IEnumerable<RegistryCredential> registries, Func<string, Task> progress, CancellationToken ct)
    {
        if (reference.StartsWith("sha256:")) return "the container was started from an image id — there is no name to pull";
        if (await _compose.LoginAllAsync(registries, progress, ct) is { } loginError) return loginError;

        await progress($"pull {reference}");
        var lastLines = new Queue<string>();
        var code = await _compose.RunAsync(["pull", reference], l =>
        {
            lastLines.Enqueue(l);
            if (lastLines.Count > 3) lastLines.Dequeue();
            return progress("  " + l);
        }, ct);
        if (code == 0) { _docker.ForgetImages(); return null; }

        var helper = await _docker.OwnImageAsync(ct);
        if (helper == null) return string.Join(" ", lastLines);
        await progress("  retrying with the host's docker login");
        string[] args = ["run", "--rm", "--entrypoint", "docker", "-v", $"{_compose.HostSocketPath}:/var/run/docker.sock",
            "--mount", $"type=bind,source={_opts.HostDockerConfig},target=/root/.docker,readonly", helper, "pull", reference];
        var (helperCode, _, err) = await _compose.CaptureAsync("docker", args, ct);
        if (helperCode == 0) { _docker.ForgetImages(); return null; }
        // "bind source path does not exist": nobody ever ran docker login on this host
        return err.Contains("does not exist") ? string.Join(" ", lastLines) : err.Trim();
    }
}
