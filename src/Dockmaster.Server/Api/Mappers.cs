using Dockmaster.Protocol;
using Dockmaster.Server.Access;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Server.Api;

public static class Mappers
{
    // SQLite drops the DateTime kind; mark stored timestamps as UTC so JSON gets the trailing "Z"
    public static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
    public static DateTime? Utc(DateTime? d) => d == null ? null : Utc(d.Value);

    public static HostStatsDto ToDto(this HostStats h) => new(h.CpuPercent, h.MemoryUsed, h.MemoryTotal, h.DiskUsed, h.DiskTotal, h.Load1, h.UptimeSeconds);

    /// <param name="job">the container is a job on a schedule (see <see cref="JobIndex"/>): finished is not stopped; its schedule and last run</param>
    public static ContainerDto ToDto(this ContainerInfo c, ImageUpdateChecker updates, JobInfoDto? job = null)
    {
        var (status, detail) = updates.Status(c);
        return new(c.Id, c.Name, c.Image, c.State, c.Health, c.Status, c.CreatedUnix, c.StartedUnix,
            c.RestartCount, c.Project, c.Service, c.CpuPercent, c.MemoryUsage, c.MemoryLimit, c.CpuLimit, c.NetRx, c.NetTx, c.Ports.ToArray(),
            c.ImageRef, status, detail, c.RestartPolicy, c.RestartMax, c.ExitCode, c.LastExit is { } e ? new ContainerExitDto(e.UnixMs, e.ExitCode, e.Oom) : null, job != null, job);
    }

    /// <summary>
    /// Unhealthy, crash-looping or dead — and a job whose last run failed (a job that finished is fine). A stopped
    /// container under always / unless-stopped was stopped by hand (Docker would have restarted it otherwise), so its
    /// past restarts are history, not a problem.
    /// </summary>
    public static bool IsProblem(this ContainerInfo c, bool job = false) =>
        (c.Health == "unhealthy" && c.State == "running") || c.State is "restarting" or "dead"
        || (c.State == "exited" && (job ? c.ExitCode != 0 || c.OomKilled
            : c.RestartCount > 0 && c.RestartPolicy is not ("always" or "unless-stopped")));

    /// <summary>
    /// The numbers a list shows for a server or a project, in one pass over the containers. Jobs waiting for their
    /// next run are neither running nor stopped: they are left out of the total and counted apart. The limits are the
    /// running containers' added up.
    /// </summary>
    public static (int Running, int Total, int Problems, int Jobs, long LimitMemory, double LimitCpus) Counts(IEnumerable<ContainerInfo> containers, Func<ContainerInfo, bool> isJob) =>
        Counts(containers, c => c, isJob);

    /// <summary>The counts of a project's containers, which come with the server each was reported by.</summary>
    public static (int Running, int Total, int Problems, int Jobs, long LimitMemory, double LimitCpus) Counts(IEnumerable<(int AgentId, ContainerInfo Container)> containers, JobIndex jobs) =>
        Counts(containers, x => x.Container, x => jobs.IsJob(x.AgentId, x.Container));

    private static (int Running, int Total, int Problems, int Jobs, long LimitMemory, double LimitCpus) Counts<T>(IEnumerable<T> items, Func<T, ContainerInfo> container, Func<T, bool> isJob)
    {
        int running = 0, total = 0, problems = 0, jobs = 0;
        long limitMemory = 0;
        var limitCpus = 0.0;
        foreach (var item in items)
        {
            var c = container(item);
            var job = isJob(item);
            var isRunning = c.State == "running";
            if (job) jobs++;
            if (isRunning)
            {
                running++;
                limitMemory += c.MemoryLimit;
                limitCpus += c.CpuLimit;
            }
            if (!job || isRunning) total++;
            if (c.IsProblem(job)) problems++;
        }
        return (running, total, problems, jobs, limitMemory, limitCpus);
    }

    /// <param name="updates">when given, AgentImage tells whether the agent's own image has a newer version</param>
    /// <param name="jobs">when given, job containers are counted apart (every list and push must pass it, or the counts differ between them)</param>
    public static AgentDto ToDto(this AgentEntity a, AgentSession? s, ImageUpdateChecker? updates = null, JobIndex? jobs = null)
    {
        var hb = s?.Last;
        var cs = hb?.Containers;
        // the agent's own container: its image status is the "is this agent outdated" answer, independent of the server version
        var self = cs?.FirstOrDefault(c => c.State == "running" && c.ImageRef.Contains("dockmaster-agent"));
        var agentImage = self != null && updates != null ? updates.Status(self).Status : "";
        var counts = Counts(cs ?? [], c => jobs?.IsJob(a.Id, c) == true);
        return new AgentDto(a.Id, a.Name, s != null, Utc(a.LastSeenAt), a.Hostname, a.Os, a.Arch, a.DockerVersion, a.Cpus, a.MemoryTotal, a.AgentIp, a.AgentVersion,
            hb?.Host?.ToDto(),
            counts.Running,
            counts.Total,
            counts.Problems,
            hb?.UnixMs ?? 0,
            s?.Host.NoCpuLimits != true,
            agentImage,
            counts.LimitMemory,
            counts.LimitCpus,
            a.MeshIp,
            a.InMesh && hb?.Mesh?.Up == true,
            counts.Jobs);
    }

    public static MeshDto ToDto(this MeshService.Snapshot snap, MeshService mesh)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var names = snap.Agents.ToDictionary(a => a.Id, a => a.Name);
        var nodes = snap.Agents.Select(a =>
        {
            var online = snap.Online(a.Id);
            var status = snap.StatusOf(a.Id);
            var cfg = snap.Plan.Configs.GetValueOrDefault(a.Id);
            var node = snap.Nodes.GetValueOrDefault(a.Id);
            var problems = new List<string>(status?.Problems ?? []);
            if (a.InMesh && online && status == null) problems.Add("no status from the agent yet (an agent older than the mesh never sends one)");
            var notes = new List<string>();
            if (status?.Backend == "wireguard-go")
            {
                // a module loaded since dm0 came up is a problem worth fixing now; a host without one (a Synology) just works slower
                if (status.KernelAvailable)
                    problems.Add("the kernel's WireGuard module is loaded now, but dm0 still runs in userspace (wireguard-go, several times slower): `docker restart dockmaster-mesh` on the host switches it over");
                else
                    notes.Add("WireGuard runs in userspace (wireguard-go): it works, but slower than the kernel module, which this host does not have loaded. If it can load one (modprobe wireguard; in a Proxmox LXC on the Proxmox host, kept with `echo wireguard > /etc/modules-load.d/wireguard.conf`), `docker restart dockmaster-mesh` afterwards switches to it");
            }
            // TCP tuning on, but this kernel cannot do BBR: the buffers still help, nothing to fix
            if (snap.Settings.TcpTuning && status is { TcpCongestion.Length: > 0 } && status.TcpCongestion != "bbr")
                notes.Add($"TCP tuning is on, but this kernel has no BBR: it keeps {status.TcpCongestion}, with the bigger buffers");
            return new MeshNodeDto(a.Id, a.Name, online, a.InMesh, a.MeshIp,
                snap.Plan.PublicIds.Contains(a.Id), a.MeshPublic,
                node == null ? null : MeshPlanner.Endpoint(node, snap.Settings), a.MeshEndpoint,
                a.MeshPort ?? snap.Settings.Port, status?.Backend ?? "", snap.Plan.IsHub(a.Id), node?.ServerHost == true,
                status?.Up == true, cfg != null && status?.ConfigHash == cfg.ConfigHash, problems.ToArray(), mesh.Supported(a.Id),
                node?.HostIps.ToArray() ?? a.HostIps.ToArray(), node?.PeerIp ?? a.PeerIp,
                snap.Networks.Where(n => n.Members.Contains(a.Id)).Select(n => n.Id).ToArray(),
                cfg?.Mtu ?? 0, a.MeshPathMtu ?? 0, notes.ToArray(), status?.TcpCongestion ?? "");
        }).ToArray();
        var pairs = snap.Plan.Paths.Select(p =>
        {
            var to = snap.Agent(p.To);
            var status = snap.StatusOf(p.From);
            // through a hub the tunnel is the one to the hub: its handshake tells whether the path is alive
            var tunnelTo = p.Kind == PathKind.ViaHub && p.HubId is { } hid ? snap.Agent(hid).MeshIp : to.MeshIp;
            var peer = status?.Peers.FirstOrDefault(x => x.MeshIp == tunnelTo);
            double? rtt = status != null && status.RttMs.TryGetValue(to.MeshIp ?? "", out var ms) && ms >= 0 ? ms : peer is { RttMs: >= 0 } ? peer.RttMs : null;
            var kind = p.Kind switch { PathKind.Direct => "direct", PathKind.Lan => "lan", PathKind.Plain => "plain", PathKind.ViaHub => "via", _ => "none" };
            var direct = p.Kind != PathKind.ViaHub;
            return new MeshPairDto(p.From, p.To, kind, p.HubId is { } h ? names.GetValueOrDefault(h) : null, rtt,
                peer is { LastHandshakeUnix: > 0 } ? Math.Max(0, now - peer.LastHandshakeUnix) : null,
                direct ? peer?.Rx ?? 0 : 0, direct ? peer?.Tx ?? 0 : 0, p.NetworkId);
        }).ToArray();
        var networks = snap.Networks.Select(n => new MeshNetworkDto(n.Id, n.Name, snap.Plan.Hubs.GetValueOrDefault(n.Id), n.HubOverride,
            n.Members.Where(id => snap.Agents.Any(a => a.Id == id && a.InMesh)).Order().ToArray(), n.Trusted)).ToArray();
        return new MeshDto(snap.Settings.Subnet, snap.Settings.Port, networks, nodes, pairs, snap.Plan.Problems.ToArray(), snap.Plan.Hints.ToArray(),
            snap.Settings.Mtu, snap.Settings.TcpTuning);
    }

    public static RegistryDto ToDto(this RegistryEntity r) => new(r.Id, r.Name, r.Url, r.Username, r.Insecure, r.PasswordProtected.Length > 0, r.Aliases);

    public static DeploymentDto ToDto(this DeploymentEntity d) => new(d.Id, d.Action, Utc(d.StartedAt), Utc(d.FinishedAt), d.Ok, d.Message, d.Source, d.ComposeYaml.Length > 0);

    /// <summary>
    /// Whether the user has a permission on a project: on every server it touches (<see cref="AccessService.ProjectServers"/>),
    /// its own when it touches none.
    /// </summary>
    public static bool Has(UserAccess access, ProjectEntity p, IReadOnlyCollection<int> servers, string perm) =>
        (servers.Count > 0 ? servers : [p.AgentId]).All(id => access.Can(id, perm));

    /// <summary>
    /// The project as this user may see it, with what they may do with it (<see cref="Has"/>). Its .env and the values
    /// of the services' environment only with Secrets.
    /// </summary>
    public static ProjectDto ToDto(this ProjectEntity p, UserAccess access, IReadOnlyCollection<int> servers)
    {
        // seeing it takes View on its own server only, which the user has to have come this far
        string[] may = [.. Perm.All.Select(x => x.Key).Where(perm => perm == Perm.View || Has(access, p, servers, perm))];
        var secrets = may.Contains(Perm.Secrets);
        var services = ComposeServices.Parse(p.ComposeYaml, p.Name);
        return new(p.Id, p.Name, p.AgentId, secrets ? p.ComposeYaml : ComposeEnv.Redact(p.ComposeYaml), secrets ? p.EnvFile : "", p.HealthTimeoutSeconds, Utc(p.UpdatedAt),
            Utc(p.LastDeployedAt), p.LastDeployOk, secrets ? [.. services] : [.. services.Select(s => s with { Environment = ComposeEnv.Redact(s.Environment) })],
            p.TemplateId, EnvHidden: !secrets, May: may);
    }

    /// <param name="hideEnv">the user lacks Secrets: the servers' compose files without the environment's values</param>
    public static ProjectPlanDto ToDto(this ProjectPlan plan, bool hideEnv) => new(
        plan.Split.Spanning,
        plan.Split.Hosts.Select(h => new HostPlanDto(h.AgentId, h.AgentName, plan.OnlineAgents.Contains(h.AgentId), plan.MeshIps.GetValueOrDefault(h.AgentId) != null,
            plan.MeshIps.GetValueOrDefault(h.AgentId), h.Services.ToArray(), h.Exposed.Select(e => new ExposedDto(e.Service, e.Ports.ToArray(), e.Address, e.Balanced)).ToArray(),
            !plan.Split.Spanning ? "" : hideEnv ? ComposeEnv.Redact(h.ComposeYaml) : h.ComposeYaml, h.Replicas.ToDictionary(), h.Frontends.Select(f => f.Service).ToArray())).ToArray(),
        plan.Split.Problems.ToArray(), plan.Split.Warnings.ToArray());

    /// <summary>Who uses what across servers: every exposed service is a link from each server of the project that does not run it.</summary>
    public static ConnectionDto ToConnection(this ProjectPlan plan) => new(
        plan.Project.Id, plan.Project.Name, plan.Split.Hosts.Select(h => h.AgentName).ToArray(),
        plan.Split.Hosts.SelectMany(provider => provider.Exposed.SelectMany(e => plan.Split.Hosts.Where(h => !h.Services.Contains(e.Service))
            .Select(consumer => new LinkDto(e.Service, consumer.AgentName, provider.AgentName, e.Ports.ToArray(), e.Address)))).ToArray(),
        plan.Split.Problems.Count > 0);
}
