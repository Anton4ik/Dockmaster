using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dockmaster.Agent.Docker;

// The Engine API calls the agent makes, one method each; the paths and bodies are Docker's (docs.docker.com/reference/api/engine).
public sealed partial class EngineApi
{
    // ---------- containers ----------

    public async Task<List<ContainerListResponse>> ListContainersAsync(bool all, CancellationToken ct, IDictionary<string, string[]>? filters = null) =>
        await GetAsync<List<ContainerListResponse>>($"containers/json?all={(all ? 1 : 0)}{(filters != null ? "&filters=" + Filters(filters) : "")}", ct) ?? [];

    /// <summary>Throws <see cref="DockerApiException"/> (404) when there is no such container.</summary>
    public async Task<ContainerInspectResponse> InspectContainerAsync(string idOrName, CancellationToken ct) =>
        (await GetAsync<ContainerInspectResponse>($"containers/{E(idOrName)}/json", ct))!;

    /// <summary>docker inspect as it comes, every field kept. Null when the container is gone.</summary>
    public Task<JsonObject?> InspectContainerRawAsync(string idOrName, CancellationToken ct) => GetNodeAsync($"containers/{E(idOrName)}/json", ct);

    /// <summary>docker create with a body as the API takes it; the new container's id.</summary>
    public async Task<string> CreateContainerAsync(string? name, JsonObject body, CancellationToken ct) =>
        (await CallAsync<IdResponse>(HttpMethod.Post, "containers/create" + (name != null ? "?name=" + E(name) : ""), body, ct)).Id;

    /// <summary>False when it was running already.</summary>
    public async Task<bool> StartContainerAsync(string id, CancellationToken ct) =>
        await CallAsync(HttpMethod.Post, $"containers/{E(id)}/start", null, ct) != HttpStatusCode.NotModified;

    /// <summary>Waits up to <paramref name="seconds"/> for it to exit, then kills it; stopped already is fine.</summary>
    public async Task StopContainerAsync(string id, int seconds, CancellationToken ct) =>
        await CallAsync(HttpMethod.Post, $"containers/{E(id)}/stop?t={seconds}", null, ct);

    public async Task RestartContainerAsync(string id, int seconds, CancellationToken ct) =>
        await CallAsync(HttpMethod.Post, $"containers/{E(id)}/restart?t={seconds}", null, ct);

    public async Task KillContainerAsync(string id, CancellationToken ct) => await CallAsync(HttpMethod.Post, $"containers/{E(id)}/kill", null, ct);

    public async Task RemoveContainerAsync(string id, bool force, CancellationToken ct) =>
        await CallAsync(HttpMethod.Delete, $"containers/{E(id)}?force={(force ? "true" : "false")}", null, ct);

    public async Task RenameContainerAsync(string id, string name, CancellationToken ct) =>
        await CallAsync(HttpMethod.Post, $"containers/{E(id)}/rename?name={E(name)}", null, ct);

    /// <summary>docker update: only what <paramref name="body"/> names changes (Memory, MemorySwap, NanoCpus, RestartPolicy...).</summary>
    public async Task UpdateContainerAsync(string id, JsonObject body, CancellationToken ct) =>
        await CallAsync(HttpMethod.Post, $"containers/{E(id)}/update", body, ct);

    /// <summary>Until the container exits; its exit code.</summary>
    public async Task<long> WaitContainerAsync(string id, CancellationToken ct) =>
        (await CallAsync<WaitResponse>(HttpMethod.Post, $"containers/{E(id)}/wait", null, ct, wait: true)).StatusCode;

    /// <summary>
    /// The container's log: <paramref name="query"/> as the API takes it (stdout=1&amp;stderr=1&amp;timestamps=1&amp;follow=1&amp;tail=100&amp;since=...);
    /// multiplexed unless the container has a TTY.
    /// </summary>
    public async Task<DockerStream> LogsAsync(string id, bool tty, string query, CancellationToken ct)
    {
        var resp = (await SendAsync(HttpMethod.Get, $"containers/{E(id)}/logs?{query}", null, ct))!;
        try { return new DockerStream(await resp.Content.ReadAsStreamAsync(ct), multiplexed: !tty); }
        catch { resp.Dispose(); throw; }
    }

    /// <summary>docker attach before the start: its stdin (when asked for), stdout and stderr; multiplexed (no TTY).</summary>
    public Task<DockerStream> AttachContainerAsync(string id, bool stdin, CancellationToken ct) =>
        HijackAsync($"containers/{E(id)}/attach?stream=1&stdin={(stdin ? 1 : 0)}&stdout=1&stderr=1", null, multiplexed: true, ct);

    // ---------- exec ----------

    /// <summary>docker exec, created; the exec's id. <paramref name="size"/>: a terminal's size from the start (rows, columns).</summary>
    public async Task<string> CreateExecAsync(string container, string[] cmd, bool stdin, bool tty, string[]? env, CancellationToken ct, (int rows, int cols)? size = null)
    {
        var body = new JsonObject
        {
            ["AttachStdin"] = stdin, ["AttachStdout"] = true, ["AttachStderr"] = true, ["Tty"] = tty,
            ["Cmd"] = new JsonArray(cmd.Select(c => (JsonNode?)c).ToArray()),
        };
        if (env != null) body["Env"] = new JsonArray(env.Select(e => (JsonNode?)e).ToArray());
        if (size is { } s) body["ConsoleSize"] = new JsonArray(s.rows, s.cols);
        return (await CallAsync<IdResponse>(HttpMethod.Post, $"containers/{E(container)}/exec", body, ct)).Id;
    }

    /// <summary>Starts the exec on a connection of its own: what it prints, and its stdin.</summary>
    public Task<DockerStream> StartExecAsync(string execId, bool tty, CancellationToken ct) =>
        HijackAsync($"exec/{E(execId)}/start", new JsonObject { ["Detach"] = false, ["Tty"] = tty }, multiplexed: !tty, ct);

    public Task<ContainerExecInspectResponse> InspectExecAsync(string execId, CancellationToken ct) =>
        CallAsync<ContainerExecInspectResponse>(HttpMethod.Get, $"exec/{E(execId)}/json", null, ct);

    public async Task ResizeExecAsync(string execId, int cols, int rows, CancellationToken ct) =>
        await CallAsync(HttpMethod.Post, $"exec/{E(execId)}/resize?h={rows}&w={cols}", null, ct);

    // ---------- images ----------

    /// <summary>Null when there is no such image.</summary>
    public Task<ImageInspectResponse?> InspectImageAsync(string nameOrId, CancellationToken ct) =>
        GetAsync<ImageInspectResponse>($"images/{E(nameOrId)}/json", ct, notFound: true);

    /// <summary>The image's inspect as it comes. Null when there is no such image.</summary>
    public Task<JsonObject?> InspectImageRawAsync(string nameOrId, CancellationToken ct) => GetNodeAsync($"images/{E(nameOrId)}/json", ct);

    public async Task<List<ImagesListResponse>> ListImagesAsync(CancellationToken ct) => await GetAsync<List<ImagesListResponse>>("images/json", ct) ?? [];

    /// <summary>Removes a tag, or the image when it is the id or its last tag; never forced.</summary>
    public async Task DeleteImageAsync(string nameOrId, CancellationToken ct) => await CallAsync(HttpMethod.Delete, $"images/{E(nameOrId)}", null, ct);

    /// <summary>Unused images: only dangling ones, or (<paramref name="allUnused"/>) tagged ones too.</summary>
    public Task<ImagesPruneResponse> PruneImagesAsync(bool allUnused, CancellationToken ct) =>
        CallAsync<ImagesPruneResponse>(HttpMethod.Post, "images/prune?filters=" + Filters(new Dictionary<string, string[]> { ["dangling"] = [allUnused ? "false" : "true"] }), null, ct, wait: true);

    // ---------- networks ----------

    public async Task<List<NetworkResponse>> ListNetworksAsync(CancellationToken ct) => await GetAsync<List<NetworkResponse>>("networks", ct) ?? [];

    public async Task CreateNetworkAsync(string name, string driver, bool @internal, CancellationToken ct) =>
        await CallAsync(HttpMethod.Post, "networks/create", new JsonObject { ["Name"] = name, ["Driver"] = driver, ["Internal"] = @internal }, ct);

    /// <summary>docker network connect, with the endpoint's settings (aliases) when given.</summary>
    public async Task ConnectNetworkAsync(string network, string container, JsonObject? endpoint, CancellationToken ct) =>
        await CallAsync(HttpMethod.Post, $"networks/{E(network)}/connect", new JsonObject { ["Container"] = container, ["EndpointConfig"] = endpoint }, ct);

    public async Task DisconnectNetworkAsync(string network, string container, CancellationToken ct) =>
        await CallAsync(HttpMethod.Post, $"networks/{E(network)}/disconnect", new JsonObject { ["Container"] = container }, ct);

    public async Task DeleteNetworkAsync(string id, CancellationToken ct) => await CallAsync(HttpMethod.Delete, $"networks/{E(id)}", null, ct);

    // ---------- volumes ----------

    public async Task<List<VolumeResponse>> ListVolumesAsync(CancellationToken ct) => (await GetAsync<VolumesListResponse>("volumes", ct))?.Volumes ?? [];

    public async Task CreateVolumeAsync(string name, IDictionary<string, string> driverOpts, IDictionary<string, string> labels, CancellationToken ct) =>
        await CallAsync(HttpMethod.Post, "volumes/create", new { Name = name, Driver = "local", DriverOpts = driverOpts, Labels = labels }, ct);

    /// <summary>Null when there is no such volume.</summary>
    public Task<VolumeResponse?> InspectVolumeAsync(string name, CancellationToken ct) => GetAsync<VolumeResponse>($"volumes/{E(name)}", ct, notFound: true);

    public async Task RemoveVolumeAsync(string name, CancellationToken ct) => await CallAsync(HttpMethod.Delete, $"volumes/{E(name)}", null, ct);

    /// <summary>docker system df for volumes only (older engines ignore the type and measure everything): volume name → bytes.</summary>
    public async Task<Dictionary<string, long>> VolumeSizesAsync(CancellationToken ct)
    {
        // measuring takes as long as the volumes are big: no request timeout
        var df = await ReadAsync(HttpMethod.Get, "system/df?type=volume", null, ReadNodeAsync, ct, wait: true);
        var sizes = new Dictionary<string, long>();
        foreach (var v in df?["Volumes"]?.AsArray() ?? [])
            if (v?["Name"]?.GetValue<string>() is { } name && v["UsageData"]?["Size"]?.GetValue<long>() is { } size) sizes[name] = size;
        return sizes;
    }

    // ---------- system ----------

    public Task<VersionResponse> VersionAsync(CancellationToken ct) => CallAsync<VersionResponse>(HttpMethod.Get, "version", null, ct);

    public Task<SystemInfoResponse> InfoAsync(CancellationToken ct) => CallAsync<SystemInfoResponse>(HttpMethod.Get, "info", null, ct);

    /// <summary>Docker's events as they happen, each handed to <paramref name="onEvent"/>, until the stream ends or is cancelled.</summary>
    public async Task MonitorEventsAsync(IDictionary<string, string[]> filters, Action<Message> onEvent, CancellationToken ct)
    {
        using var resp = (await SendAsync(HttpMethod.Get, "events?filters=" + Filters(filters), null, ct))!;
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            if (JsonSerializer.Deserialize<Message>(line, Json) is { } m) onEvent(m);
        }
    }

    private sealed record IdResponse(string Id);
    private sealed record WaitResponse(long StatusCode);
}
