using System.Buffers.Text;
using System.Collections.Concurrent;

namespace Dockmaster.Agent.Docker;

/// <summary>
/// The most memory each container has held since it started, from the kernel's memory.peak (cgroup v2; docker's stats
/// have it only on cgroup v1, as max_usage). A spike between two heartbeats is in it — the one the OOM killer acts on
/// and an average a minute never shows. The agent's own /sys/fs/cgroup is its own cgroup only, so it reads the host's
/// tree mounted at <see cref="HostMount"/>; without that mount, on cgroup v1 or on a kernel before 5.19 every answer
/// is 0. One open and read of a small file per running container per heartbeat, the file found once.
/// </summary>
public sealed class CgroupPeaks
{
    public const string HostMount = "/host/cgroup";

    // a container whose file was not found (a cgroup parent of its own, stopping right then) is looked for again after this
    private static readonly TimeSpan Retry = TimeSpan.FromMinutes(5);

    private readonly string? _root;
    private readonly ConcurrentDictionary<string, (string? Path, DateTime At)> _files = new();

    public CgroupPeaks() : this(DockerService.InContainer ? HostMount : "/sys/fs/cgroup") { }

    /// <param name="root">the host's cgroup v2 tree</param>
    public CgroupPeaks(string root) => _root = File.Exists(Path.Combine(root, "cgroup.controllers")) ? root : null;

    /// <summary>The host's cgroup v2 tree is there to read.</summary>
    public bool Available => _root != null;

    /// <summary>Bytes; 0 when it cannot be read.</summary>
    public long Peak(string id)
    {
        if (_root == null) return 0;
        var now = DateTime.UtcNow;
        var f = _files.TryGetValue(id, out var known) && (known.Path != null || now - known.At < Retry) ? known : _files[id] = (Find(id), now);
        if (f.Path == null) return 0;
        try
        {
            using var h = File.OpenHandle(f.Path);
            Span<byte> buf = stackalloc byte[24];
            var n = RandomAccess.Read(h, buf, 0);
            return Utf8Parser.TryParse(buf[..n], out long v, out _) ? v : 0;
        }
        // gone with the container (restarting: the next heartbeat finds it again)
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>Forgets the containers not running any more.</summary>
    public void Keep(IReadOnlySet<string> running)
    {
        foreach (var id in _files.Keys.Where(id => !running.Contains(id))) _files.TryRemove(id, out _);
    }

    // the systemd cgroup driver's place, then cgroupfs'
    private string? Find(string id) =>
        new[] { $"{_root}/system.slice/docker-{id}.scope/memory.peak", $"{_root}/docker/{id}/memory.peak" }.FirstOrDefault(File.Exists);
}
