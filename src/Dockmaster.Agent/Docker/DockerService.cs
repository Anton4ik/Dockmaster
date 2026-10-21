using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Dockmaster.Protocol;

namespace Dockmaster.Agent.Docker;

/// <summary>Typed access to the local Docker engine (inspect, stats, actions, logs).</summary>
public sealed partial class DockerService
{
    public const string ProjectLabel = "com.docker.compose.project";
    public const string ServiceLabel = "com.docker.compose.service";
    // the platform image compose created the container from (on the containerd store: the manifest, not the index)
    public const string ComposeImageLabel = "com.docker.compose.image";
    public const string ConfigHashLabel = "com.docker.compose.config-hash";
    public const string NumberLabel = "com.docker.compose.container-number";
    public const string WorkingDirLabel = "com.docker.compose.project.working_dir";
    public const string ConfigFilesLabel = "com.docker.compose.project.config_files";
    public const string EnvironmentFileLabel = "com.docker.compose.project.environment_file";

    /// <summary>How long a stop or restart waits for the container to exit before Docker kills it.</summary>
    public const int StopTimeoutSeconds = 30;

    private readonly EngineApi _engine;
    private readonly ILogger<DockerService> _log;

    public DockerService(IOptions<AgentOptions> opts, ILogger<DockerService> log)
    {
        _log = log;
        _engine = new EngineApi(opts.Value.DockerSocket);
    }

    /// <summary>The Engine API itself, for the stores (volumes, networks, images, exec).</summary>
    public EngineApi Api => _engine;

    public Task<VersionResponse> VersionAsync(CancellationToken ct) => _engine.VersionAsync(ct);
    public Task<SystemInfoResponse> InfoAsync(CancellationToken ct) => _engine.InfoAsync(ct);

    public async Task ContainerActionAsync(string id, ContainerOp op, CancellationToken ct)
    {
        // a stopped agent cannot be started again from here, and nothing would bring it back
        if (op is ContainerOp.ContainerStop or ContainerOp.ContainerKill or ContainerOp.ContainerRemove && await GuardedAsync(id, ct) is { } guarded)
            throw new InvalidOperationException($"{guarded}: stop or remove it on the host");
        switch (op)
        {
            case ContainerOp.ContainerStart: await StartAsync(id, ct); break;
            case ContainerOp.ContainerStop: await StopAsync(id, StopTimeoutSeconds, ct); break;
            case ContainerOp.ContainerRestart: await _engine.RestartContainerAsync(id, StopTimeoutSeconds, ct); break;
            case ContainerOp.ContainerKill: await _engine.KillContainerAsync(id, ct); break;
            case ContainerOp.ContainerRemove: await RemoveAsync(id, ct); break;
            default: throw new ArgumentOutOfRangeException(nameof(op), op, "unknown container action");
        }
    }

    /// <summary>Docker's own spelling of a restart policy, "no" for none (docker writes "" for it).</summary>
    public static string RestartPolicyName(RestartPolicy? p) => RestartPolicyName(p?.Name ?? "");

    public static string RestartPolicyName(string name) => name is "always" or "unless-stopped" or "on-failure" ? name : "no";

    public Task<ContainerInspectResponse> InspectAsync(string id, CancellationToken ct) => _engine.InspectContainerAsync(id, ct);

    /// <summary>The container page's data (see <see cref="ContainerDetails"/>); by id or name.</summary>
    public async Task<string> InspectJsonAsync(string idOrName, CancellationToken ct)
    {
        var c = await InspectAsync(idOrName, ct);
        return ContainerDetails.ToJson(c, await TryInspectImageAsync(c.Image, ct)); // image removed since: no marks
    }

    public async Task<ImageInspectResponse> InspectImageAsync(string nameOrId, CancellationToken ct) =>
        await _engine.InspectImageAsync(nameOrId, ct) ?? throw new DockerApiException(HttpStatusCode.NotFound, $"No such image: {nameOrId}");

    /// <summary>The image, or null when Docker cannot say (removed since the container was created).</summary>
    public async Task<ImageInspectResponse?> TryInspectImageAsync(string nameOrId, CancellationToken ct)
    {
        try { return await _engine.InspectImageAsync(nameOrId, ct); }
        catch (DockerApiException) { return null; }
    }

    /// <summary>docker update: new memory / CPU limits for a running container, no restart.</summary>
    public async Task UpdateResourcesAsync(string id, long memory, double cpus, CancellationToken ct)
    {
        var p = new JsonObject();
        if (memory > 0)
        {
            var current = await _engine.InspectContainerAsync(id, ct);
            p["Memory"] = memory;
            // swap must stay >= memory: keep "unlimited", keep the swap on top of memory the container had, else docker's default of twice the memory
            var h = current.HostConfig;
            p["MemorySwap"] = h.MemorySwap == -1 ? -1 : h.MemorySwap > 0 && h.Memory > 0 ? memory + Math.Max(0, h.MemorySwap - h.Memory) : memory * 2;
        }
        if (cpus > 0) p["NanoCpus"] = (long)Math.Round(cpus * 1e9);
        await _engine.UpdateContainerAsync(id, p, ct);
    }

    /// <summary>docker update --restart: a new restart policy, running or not, no restart.</summary>
    public async Task<string> UpdateRestartPolicyAsync(string id, string policy, int maxRetries, CancellationToken ct)
    {
        if (policy is not ("no" or "always" or "unless-stopped" or "on-failure")) throw new ArgumentException($"unknown restart policy '{policy}'");
        var max = policy == "on-failure" ? Math.Max(0, maxRetries) : 0;
        await _engine.UpdateContainerAsync(id, new JsonObject { ["RestartPolicy"] = new JsonObject { ["Name"] = policy, ["MaximumRetryCount"] = max } }, ct);
        return $"restart policy set to {policy}" + (max > 0 ? $" (max {max})" : "");
    }

    /// <summary>docker inspect as Docker sent it (for <see cref="ContainerClone"/>). Null when the container is gone.</summary>
    public Task<JsonObject?> InspectRawAsync(string idOrName, CancellationToken ct) => _engine.InspectContainerRawAsync(idOrName, ct);

    /// <summary>An image's inspect as Docker sent it. Null when there is no such image (removed since).</summary>
    public Task<JsonObject?> InspectImageRawAsync(string nameOrId, CancellationToken ct) => _engine.InspectImageRawAsync(nameOrId, ct);

    /// <summary>
    /// Creates a copy of <paramref name="old"/> (raw inspect) from <see cref="ContainerClone.Parameters"/> and joins
    /// the networks beyond the first; a copy that cannot join them is removed again.
    /// </summary>
    public async Task<string> CreateCloneAsync(string name, JsonObject parameters, JsonObject old, CancellationToken ct, string? aliasFrom = null)
    {
        var id = await _engine.CreateContainerAsync(name, parameters, ct);
        try
        {
            foreach (var (network, endpoint) in ContainerClone.OtherNetworks(old, name, aliasFrom))
                await _engine.ConnectNetworkAsync(network, id, endpoint, ct);
        }
        catch
        {
            await TryRemoveAsync(id, CancellationToken.None);
            throw;
        }
        return id;
    }

    public Task RenameAsync(string id, string name, CancellationToken ct) => _engine.RenameContainerAsync(id, name, ct);

    /// <summary>Starts a container; false when it was running already.</summary>
    public Task<bool> StartAsync(string id, CancellationToken ct) => _engine.StartContainerAsync(id, ct);

    public Task StopAsync(string id, int timeoutSeconds, CancellationToken ct) => _engine.StopContainerAsync(id, timeoutSeconds, ct);

    public Task RemoveAsync(string id, CancellationToken ct) => _engine.RemoveContainerAsync(id, force: true, ct);

    /// <summary>Removes a container if it can; for clean-up after a failure, where one more error changes nothing.</summary>
    public async Task TryRemoveAsync(string id, CancellationToken ct)
    {
        try { await RemoveAsync(id, ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { }
    }

    /// <summary>Streams container logs (demultiplexed) to the callback until EOF or cancel.</summary>
    public async Task StreamLogsAsync(string id, int tail, bool follow, string? since, Func<string, Task> onText, CancellationToken ct)
    {
        var query = $"stdout=1&stderr=1&timestamps=1&follow={(follow ? 1 : 0)}&tail={(tail > 0 ? tail.ToString() : "all")}";
        if (!string.IsNullOrEmpty(since)) query += "&since=" + Uri.EscapeDataString(since);
        var insp = await _engine.InspectContainerAsync(id, ct);
        using var stream = await _engine.LogsAsync(id, insp.Config.Tty, query, ct);
        await stream.ReadTextAsync(onText, ct);
    }

    /// <summary>Runs a shell command inside a container (used for pre-stop hooks).</summary>
    public async Task<(long exitCode, string output)> ExecAsync(string id, string shellCommand, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var exec = await _engine.CreateExecAsync(id, ["sh", "-c", shellCommand], stdin: false, tty: false, env: null, cts.Token);
        string stdout, stderr;
        using (var stream = await _engine.StartExecAsync(exec, false, cts.Token))
        using (cts.Token.Register(stream.Dispose)) // a read waits on the socket: closing it ends the wait
        {
            try { (stdout, stderr) = await stream.ReadOutputToEndAsync(cts.Token); }
            catch (Exception) when (cts.IsCancellationRequested) { throw new OperationCanceledException(cts.Token); }
        }
        var insp = await _engine.InspectExecAsync(exec, cts.Token);
        return (insp.ExitCode, stdout + stderr);
    }
}
