using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Dockmaster.Protocol;

namespace Dockmaster.Agent.Docker;

/// <summary>Docker volumes on this host: what is there, how big, who uses it; creating and removing them.</summary>
public sealed partial class VolumeStore
{
    public const string ComposeVolumeLabel = "com.docker.compose.volume";
    private const string AnonymousLabel = "com.docker.volume.anonymous";
    private const string HelperLabel = "dockmaster.helper";
    private const int Chunk = 64 * 1024;

    private readonly DockerService _docker;
    private readonly EngineApi _api;
    private readonly ILogger<VolumeStore> _log;

    public VolumeStore(DockerService docker, ILogger<VolumeStore> log)
    {
        _docker = docker;
        _api = docker.Api;
        _log = log;
    }

    /// <summary>A container that mounts a volume: where, and whether it may write.</summary>
    public sealed record VolumeUser(string Name, string Project, string Service, string Destination, bool Rw, bool Running);

    /// <param name="Anonymous">made by docker for a VOLUME of an image nobody named: a re-created container gets a new, empty one</param>
    /// <param name="Size">bytes, -1 = not measured</param>
    /// <param name="Share">"nfs" / "cifs" and where from, for a volume on another machine; null = on this host</param>
    public sealed record VolumeEntry(string Name, string Driver, string Mountpoint, long Created, string Project, string ComposeName, bool Anonymous,
        long Size, VolumeUser[] Containers, string? Share, Dictionary<string, string> Labels);

    public sealed record RemoveOutcome(string[] Removed, Dictionary<string, string> Failed);

    public async Task<string> ListJsonAsync(ListVolumes req, CancellationToken ct)
    {
        var volumes = await _api.ListVolumesAsync(ct);
        var users = await UsersAsync(ct);
        var sizes = req.Sizes ? await SizesAsync(ct) : null;
        var list = volumes.Select(v =>
        {
            var labels = new Dictionary<string, string>(v.Labels ?? new Dictionary<string, string>());
            return new VolumeEntry(v.Name, v.Driver, v.Mountpoint, Created(v.CreatedAt),
                labels.GetValueOrDefault(DockerService.ProjectLabel, ""), labels.GetValueOrDefault(ComposeVolumeLabel, ""),
                labels.ContainsKey(AnonymousLabel) || (labels.Count == 0 && AnonymousNameRegex().IsMatch(v.Name)),
                sizes?.GetValueOrDefault(v.Name, -1) ?? -1, users.GetValueOrDefault(v.Name, []), Share(v.Options), labels);
        }).OrderBy(v => v.Anonymous).ThenBy(v => v.Name, StringComparer.Ordinal).ToList();

        var images = new Dictionary<string, string[]>();
        foreach (var image in req.Images.Distinct())
        {
            // not pulled on this host: nothing to say
            if (await _api.InspectImageAsync(image, ct) is { } found) images[image] = found.Config?.Volumes?.Keys.Order().ToArray() ?? [];
        }
        return JsonSerializer.Serialize(new { volumes = list, images }, AgentJson.Web);
    }

    /// <summary>The containers (running or stopped) that mount the volume.</summary>
    public async Task<VolumeUser[]> UsersOfAsync(string volume, CancellationToken ct) => (await UsersAsync(ct)).GetValueOrDefault(volume, []);

    /// <summary>Volume name → the containers (running or stopped) that mount it.</summary>
    private async Task<Dictionary<string, VolumeUser[]>> UsersAsync(CancellationToken ct)
    {
        var containers = await _api.ListContainersAsync(true, ct);
        return containers
            .SelectMany(c => (c.Mounts ?? []).Where(m => m.Type == "volume" && !string.IsNullOrEmpty(m.Name)).Select(m => (m.Name, User: new VolumeUser(
                c.DisplayName(), Label(c.Labels, DockerService.ProjectLabel), Label(c.Labels, DockerService.ServiceLabel),
                m.Destination, m.RW, c.State == "running"))))
            .GroupBy(x => x.Name)
            .ToDictionary(g => g.Key, g => g.Select(x => x.User).OrderBy(u => u.Name, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Sizes from the engine's disk usage (`docker system df`); null when docker cannot say.</summary>
    private async Task<Dictionary<string, long>?> SizesAsync(CancellationToken ct)
    {
        try { return await _api.VolumeSizesAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning("volume sizes: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Creates a volume. A share of another machine (options type=nfs / cifs) is mounted once by a short-lived helper
    /// container right away: a wrong address or password shows now, not when an app starts; it is removed again when
    /// that fails.
    /// </summary>
    public async Task<string> CreateAsync(CreateVolume req, Func<string, Task> log, CancellationToken ct)
    {
        if (!VolumeName.IsValid(req.Name)) throw new ArgumentException($"'{req.Name}' is not a volume name: letters, digits, '_', '.' and '-'");
        var existing = await _api.ListVolumesAsync(ct);
        if (existing.Any(v => v.Name == req.Name)) throw new InvalidOperationException($"a volume named {req.Name} already exists on this server");
        await _api.CreateVolumeAsync(req.Name, new Dictionary<string, string>(req.Options), new Dictionary<string, string>(req.Labels), ct);
        if (req.Options.Count == 0) return $"volume {req.Name} created";
        await log($"mounting {req.Name} once to check the share…");
        if (await TryMountAsync(req.Name, ct) is { } problem)
        {
            try { await _api.RemoveVolumeAsync(req.Name, ct); } catch (Exception ex) { _log.LogDebug(ex, "remove {Volume}", req.Name); }
            throw new InvalidOperationException($"the share could not be mounted, nothing was created: {problem}");
        }
        return $"volume {req.Name} created, the share mounts";
    }

    /// <summary>Starts a helper container with the volume mounted; the error when that fails.</summary>
    private async Task<string?> TryMountAsync(string volume, CancellationToken ct)
    {
        if (await _docker.OwnImageAsync(ct) is null) return null; // not in a container: cannot tell
        try
        {
            await HelperAsync(volume, true, ["ls", "/v"], null, null, null, ct);
            return null;
        }
        catch (InvalidOperationException ex) { return ex.Message; }
    }

    /// <summary>
    /// Runs one command in a helper container from the agent's own image with the volume at /v: feeds stdin from
    /// `input` (calling `written` after each chunk), hands stdout to `output`, returns the exit code and what it wrote
    /// to stderr. A read-only helper needs the volume to exist: binding a missing one would create it. The helper is
    /// removed in any case.
    /// </summary>
    /// <param name="output">gets each piece of stdout as it comes; the memory is the read buffer, not kept past the call</param>
    public async Task<(long code, string errors)> HelperAsync(string volume, bool readOnly, string[] cmd, ChannelReader<DataChunk>? input,
        Func<Task>? written, Func<ReadOnlyMemory<byte>, Task>? output, CancellationToken ct)
    {
        if (!VolumeName.IsValid(volume)) throw new ArgumentException($"'{volume}' is not a volume name");
        if (await _docker.OwnImageAsync(ct) is not { } image) throw new InvalidOperationException("the agent does not run in a container: it has no image to start a helper from");
        string? id = null;
        try
        {
            if (readOnly && await _api.InspectVolumeAsync(volume, ct) == null) throw new InvalidOperationException($"no volume {volume} on this server");
            // not cancelled half way: a helper docker has made must be known here, or it is never removed
            id = await _api.CreateContainerAsync(null, new JsonObject
            {
                ["Image"] = image,
                ["Entrypoint"] = new JsonArray(cmd.Select(c => (JsonNode?)c).ToArray()),
                ["AttachStdin"] = input != null, ["OpenStdin"] = input != null, ["StdinOnce"] = input != null,
                ["AttachStdout"] = true, ["AttachStderr"] = true,
                ["Labels"] = new JsonObject { [HelperLabel] = "volume" },
                // what it prints is the volume's bytes: not for the host's container logs
                ["HostConfig"] = new JsonObject
                {
                    ["Binds"] = new JsonArray($"{volume}:/v{(readOnly ? ":ro" : "")}"),
                    ["NetworkMode"] = "none",
                    ["LogConfig"] = new JsonObject { ["Type"] = "none" },
                },
            }, CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            using var stream = await _api.AttachContainerAsync(id, input != null, ct);
            await _api.StartContainerAsync(id, ct);

            using var feedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var feeding = input == null ? Task.CompletedTask : Task.Run(async () =>
            {
                await foreach (var chunk in input.ReadAllAsync(feedCts.Token))
                {
                    if (chunk.Data.Length > 0)
                    {
                        var bytes = chunk.Data.ToByteArray();
                        await stream.WriteAsync(bytes, 0, bytes.Length, feedCts.Token);
                        if (written != null) await written();
                    }
                    if (chunk.Eof) break;
                }
                stream.CloseWrite();
            }, feedCts.Token);

            var errors = new StringBuilder();
            var buffer = new byte[Chunk];
            while (true)
            {
                var r = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct);
                if (r.EOF) break;
                if (r.Target == DockerStream.Target.StandardError) { if (errors.Length < 4000) errors.Append(Encoding.UTF8.GetString(buffer, 0, r.Count)); }
                else if (output != null) await output(buffer.AsMemory(0, r.Count));
            }
            // the command is done: bytes still on the way have nowhere to go. One that stopped early (tar on a broken
            // archive) broke the pipe: its exit code and stderr tell why, not the broken pipe.
            feedCts.Cancel();
            try { await feeding; }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogDebug("feeding helper {Id}: {Error}", id, ex.Message); }
            return (await _api.WaitContainerAsync(id, ct), errors.ToString().Trim());
        }
        catch (DockerApiException ex) { throw new InvalidOperationException(ex.Message); }
        finally
        {
            if (id != null)
                try { await _api.RemoveContainerAsync(id, true, CancellationToken.None); }
                catch (Exception ex) { _log.LogDebug(ex, "remove helper {Id}", id); }
        }
    }

    /// <summary>Removes the helpers an agent that stopped mid-transfer left behind; problems are only logged.</summary>
    public async Task RemoveLeftoverHelpersAsync(CancellationToken ct)
    {
        try
        {
            var left = await _api.ListContainersAsync(true, ct, new Dictionary<string, string[]> { ["label"] = [HelperLabel] });
            foreach (var c in left)
            {
                try
                {
                    await _api.RemoveContainerAsync(c.ID, true, ct);
                    _log.LogInformation("removed volume helper {Name}, left behind by an earlier run", c.DisplayName());
                }
                catch (DockerApiException ex) { _log.LogWarning("removing volume helper {Id} failed: {Error}", c.ID, ex.Message); }
            }
        }
        catch (Exception ex) { _log.LogWarning("looking for volume helpers left behind failed: {Error}", ex.Message); }
    }

    /// <summary>Removes the given volumes; a volume any container mounts (running or stopped) is refused.</summary>
    public async Task<string> RemoveJsonAsync(IEnumerable<string> names, CancellationToken ct)
    {
        var users = await UsersAsync(ct);
        var removed = new List<string>();
        var failed = new Dictionary<string, string>();
        foreach (var name in names.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            if (users.TryGetValue(name, out var by)) { failed[name] = "used by " + string.Join(", ", by.Select(u => u.Name)); continue; }
            try
            {
                await _api.RemoveVolumeAsync(name, ct);
                removed.Add(name);
            }
            catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { removed.Add(name); }
            catch (DockerApiException ex) { failed[name] = ex.Message; }
        }
        return JsonSerializer.Serialize(new RemoveOutcome([.. removed], failed), AgentJson.Web);
    }

    /// <summary>"nfs 10.0.0.5:/export" for a volume of the local driver that mounts a share; null for plain local volumes.</summary>
    private static string? Share(IDictionary<string, string>? options)
    {
        if (options == null || !options.TryGetValue("type", out var type) || type is not ("nfs" or "nfs4" or "cifs")) return null;
        var device = Label(options, "device");
        var addr = Label(options, "o").Split(',').FirstOrDefault(o => o.StartsWith("addr="))?["addr=".Length..];
        return $"{type} {(addr != null && device.StartsWith(':') ? addr + device : device)}".Trim();
    }

    private static string Label(IDictionary<string, string>? map, string key) => map != null && map.TryGetValue(key, out var v) ? v : "";

    private static long Created(string? created) =>
        DateTimeOffset.TryParse(created, out var at) ? at.ToUnixTimeSeconds() : 0;

    [GeneratedRegex("^[0-9a-f]{64}$")] private static partial Regex AnonymousNameRegex();
}
