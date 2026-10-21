using System.Text.RegularExpressions;

namespace Dockmaster.Agent.Docker;

// The agent's own container: who it is, which image it runs, and whether its data dir is mounted the way compose needs.
public sealed partial class DockerService
{
    /// <summary>The agent runs inside a container (the normal case).</summary>
    public static bool InContainer { get; } = File.Exists("/.dockerenv");

    /// <summary>
    /// The agent's own container id. The hostname is not reliable (Portainer's recreate copies the hostname of the
    /// original container); docker mounts /etc/hostname from /var/lib/docker/containers/&lt;id&gt;/, which mountinfo shows.
    /// </summary>
    public static string SelfId { get; } = ReadSelfId();

    private static string ReadSelfId()
    {
        try { return SelfIdFrom(File.ReadLines("/proc/self/mountinfo")) ?? Environment.MachineName; }
        catch { return Environment.MachineName; }
    }

    /// <summary>
    /// The container id from the mountinfo lines: the one right before "/hostname" on the line mounted at /etc/hostname.
    /// Nested docker (docker-in-docker, agents on a host whose docker root sits under another container's path) shows
    /// an outer container's id earlier in that path and in other lines, so neither the first match nor any line will do.
    /// </summary>
    internal static string? SelfIdFrom(IEnumerable<string> mountinfo)
    {
        string? any = null;
        foreach (var line in mountinfo)
        {
            var fields = line.Split(' ');
            if (fields.Length > 4 && fields[4] == "/etc/hostname" && HostnameIdRegex().Match(fields[3]) is { Success: true } h) return h.Groups[1].Value;
            if (any == null && SelfIdRegex().Match(line) is { Success: true } m) any = m.Groups[1].Value;
        }
        return any;
    }

    [GeneratedRegex("/containers/([0-9a-f]{64})/")] private static partial Regex SelfIdRegex();
    [GeneratedRegex("/containers/([0-9a-f]{64})/hostname$")] private static partial Regex HostnameIdRegex();

    /// <summary>The container is the agent itself (SelfId may be the short hostname when mountinfo was not readable).</summary>
    public static bool IsSelf(string containerId) => InContainer && containerId.StartsWith(SelfId, StringComparison.Ordinal);

    /// <summary>
    /// Why the container may not get a shell or be stopped, killed or removed from the UI, null for any other one: the
    /// agent holds docker.sock and every project's secrets (a shell in it is root on the host and more than the
    /// Terminal permission gives), the mesh sidecar the host's network and WireGuard keys.
    /// </summary>
    public async Task<string?> GuardedAsync(string idOrName, CancellationToken ct) =>
        Guarded(await _engine.InspectContainerAsync(idOrName, ct), InContainer ? SelfId : null);

    internal static string? Guarded(ContainerInspectResponse c, string? selfId) =>
        selfId != null && c.ID.StartsWith(selfId, StringComparison.Ordinal) ? "this is the Dockmaster agent itself"
        : c.Name.TrimStart('/') == MeshSidecar.Name ? "this is the agent's mesh sidecar"
        : null;

    /// <summary>Image id of the agent's own container (null when the agent does not run in a container).</summary>
    public async Task<string?> OwnImageAsync(CancellationToken ct)
    {
        if (!InContainer) return null;
        try { return (await _engine.InspectContainerAsync(SelfId, ct)).Image; }
        catch (Exception ex) { _log.LogDebug(ex, "inspect own container"); return null; }
    }

    /// <summary>
    /// Compose passes project paths (secrets, ./relative binds) to the daemon, which resolves them on the host, so the
    /// agent's data dir must be mounted from the same path it has inside. Returns what is wrong, or null when it is
    /// fine or cannot be told (agent not in a container).
    /// </summary>
    public async Task<string?> DataDirProblemAsync(string dataDir, CancellationToken ct)
    {
        if (!InContainer) return null;
        var target = Path.TrimEndingDirectorySeparator(dataDir);
        try
        {
            var self = await _engine.InspectContainerAsync(SelfId, ct);
            var source = self.Mounts?.FirstOrDefault(m => Path.TrimEndingDirectorySeparator(m.Destination) == target)?.Source;
            if (source == target) return null;
            var where = source == null ? "is not mounted from the host" : $"is mounted from {source}";
            return $"agent data dir {target} {where} — secrets and relative bind mounts will not reach the containers; run the agent with -v {target}:{target}";
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "inspect own container");
            return null;
        }
    }
}
