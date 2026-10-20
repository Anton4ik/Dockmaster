using System.Net;
using System.Text.Json.Nodes;

namespace Dockmaster.Agent.Docker;

/// <summary>
/// The "dockmaster-mesh" container that owns dm0: the agent's own image with dm-mesh as entrypoint, in the host
/// network namespace with NET_ADMIN. Keeps it existing and current, and runs dm-mesh commands in it.
/// </summary>
internal sealed class MeshSidecar
{
    public const string Name = "dockmaster-mesh";
    private const string SpecLabel = "dockmaster.mesh.spec";
    private const string Spec = "2"; // bump when the sidecar's create parameters change, so EnsureAsync re-creates it
    private const string DirLabel = "dockmaster.mesh.dir";

    private readonly DockerService _docker;
    private readonly string _meshDir;
    private readonly ILogger _log;

    public MeshSidecar(DockerService docker, string meshDir, ILogger log)
    {
        _docker = docker;
        _meshDir = meshDir;
        _log = log;
    }

    /// <summary>Runs `dm-mesh args` in the sidecar and returns its output; a non-zero exit throws with the last line.</summary>
    public async Task<string> ExecAsync(string args, TimeSpan timeout, CancellationToken ct)
    {
        var (code, output) = await _docker.ExecAsync(Name, "dm-mesh " + args, timeout, ct);
        if (code != 0) throw new InvalidOperationException($"dm-mesh {args.Split(' ')[0]} failed: {DockerErrors.LastLine(output) ?? "no output"}");
        return output;
    }

    public async Task<ContainerInspectResponse?> FindAsync(CancellationToken ct)
    {
        try { return await _docker.InspectAsync(Name, ct); }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public Task RemoveAsync(CancellationToken ct) => _docker.RemoveAsync(Name, ct);

    /// <summary>The sidecar exists, runs the agent's current image and is up.</summary>
    public async Task EnsureAsync(Func<string, Task>? progress, CancellationToken ct)
    {
        var image = await _docker.OwnImageAsync(ct) ?? throw new InvalidOperationException("the mesh needs the agent to run in a container (the sidecar uses its image)");
        var existing = await FindAsync(ct);
        // One mesh per host: the sidecar and dm0 are host-wide. A second agent on the same host (another Dockmaster
        // instance, a test) must not take over the first one's tunnel — replacing it broke a live mesh once.
        if (existing != null && OwnerDir(existing) is { } owner && owner != _meshDir)
            throw new InvalidOperationException($"another Dockmaster agent on this host already runs the mesh (its data is in {owner}); one host can be in one mesh only");
        if (existing != null)
        {
            if (existing.Image == image && existing.Config.Labels.Get(SpecLabel) == Spec)
            {
                if (!existing.State.Running) await _docker.StartAsync(existing.ID, ct);
                return;
            }
            // agent updated: the sidecar follows (kernel WireGuard keeps the interface, wireguard-go blinks for a moment)
            if (progress != null) await progress("re-creating the mesh sidecar with the current agent image");
            await _docker.RemoveAsync(existing.ID, ct);
        }
        else if (progress != null) await progress("creating the mesh sidecar (host network, NET_ADMIN)");

        // Best setup first, then without what the host cannot give. Docker reports a missing device or a refused
        // security option only when the container starts (a Proxmox LXC has no /dev/net/tun), so each try is create + start.
        (bool tun, bool unconfined)[] tries = [(true, true), (false, true), (true, false), (false, false)];
        for (var i = 0; ; i++)
        {
            var (tun, unconfined) = tries[i];
            string? id = null;
            try
            {
                id = await _docker.Api.CreateContainerAsync(Name, Parameters(image, tun, unconfined), ct);
                await _docker.StartAsync(id, ct);
                if (progress != null && (!tun || !unconfined))
                    await progress($"sidecar runs without {(tun ? "" : "/dev/net/tun (kernel WireGuard only)")}{(!tun && !unconfined ? " and " : "")}{(unconfined ? "" : "writable /proc/sys")}");
                return;
            }
            catch (DockerApiException ex) when (i < tries.Length - 1 && (ex.Message.Contains("/dev/net/tun") || ex.Message.Contains("apparmor", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("/proc/sys")))
            {
                _log.LogInformation("mesh sidecar with tun={Tun} unconfined={Unconfined} failed: {Error}", tun, unconfined, ex.Message);
                if (id != null) await _docker.RemoveAsync(id, ct);
            }
        }
    }

    /// <summary>The mesh folder the sidecar belongs to: its label, or for sidecars from before the label its bind mount.</summary>
    private static string? OwnerDir(ContainerInspectResponse sidecar) =>
        sidecar.Config.Labels != null && sidecar.Config.Labels.TryGetValue(DirLabel, out var dir) ? dir
        : sidecar.Mounts?.FirstOrDefault(m => m.Destination.EndsWith("/mesh", StringComparison.Ordinal))?.Source;

    private JsonObject Parameters(string image, bool withTun, bool unconfinedSysctl)
    {
        var host = new JsonObject
        {
            ["NetworkMode"] = "host",
            ["CapAdd"] = new JsonArray("NET_ADMIN"),
            ["Devices"] = withTun ? new JsonArray(new JsonObject { ["PathOnHost"] = "/dev/net/tun", ["PathInContainer"] = "/dev/net/tun", ["CgroupPermissions"] = "rwm" }) : new JsonArray(),
            // the data dir is mounted at the same path on the host (the agent checks that), so this is the host path too
            ["Binds"] = new JsonArray($"{_meshDir}:{_meshDir}"),
            ["RestartPolicy"] = new JsonObject { ["Name"] = "always" },
            // uses 1-15 MB; a cap keeps a leak from eating the host (no CPU cap: it carries all cross-host traffic)
            ["Memory"] = 128L << 20,
            ["SecurityOpt"] = unconfinedSysctl ? new JsonArray("apparmor=unconfined") : new JsonArray(),
        };
        // /proc/sys writable: ip_forward on a hub, nonlocal_bind for ports published on the mesh ip. Two things stand in
        // the way: docker's read-only /proc/sys (the CLI's systempaths=unconfined, which the API only takes as empty
        // Masked/ReadonlyPaths) and the docker-default AppArmor profile, which denies writes there on its own.
        if (unconfinedSysctl)
        {
            host["MaskedPaths"] = new JsonArray();
            host["ReadonlyPaths"] = new JsonArray();
        }
        return new JsonObject
        {
            ["Image"] = image,
            ["Entrypoint"] = new JsonArray("dm-mesh", "run"),
            ["Env"] = new JsonArray($"DM_MESH_DIR={_meshDir}"),
            ["Labels"] = new JsonObject { [SpecLabel] = Spec, ["dockmaster.mesh"] = "1", [DirLabel] = _meshDir },
            ["HostConfig"] = host,
        };
    }
}
