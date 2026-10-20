using System.Text.Json;
using Microsoft.Extensions.Options;
using Dockmaster.Protocol;

namespace Dockmaster.Agent.Docker;

/// <summary>
/// The host's side of the mesh: keys and config files under &lt;DataDir&gt;/mesh, and the "dockmaster-mesh" sidecar that
/// owns the dm0 interface. The sidecar runs the agent's own image in the host network namespace with NET_ADMIN, so
/// the agent container itself stays unprivileged and existing agents need no new `docker run` flags. Everything
/// on the host is done by the dm-mesh script inside the sidecar (docker exec); this class only writes files and
/// keeps the sidecar alive.
/// </summary>
public sealed class MeshManager : BackgroundService
{
    private const string PrivateKeyFile = "private.key";
    private const string PublicKeyFile = "public.key";
    private const string ConfFile = "dm0.conf";
    private const string EnvFile = "dm0.env";
    private const string PeersFile = "peers.txt";
    private const string ForwardFile = "dm0.fwd";
    private const string RoutesFile = "dm0.routes";
    private const string HaproxyFile = "dm0.haproxy.cfg";
    private const string WrittenByFile = "written-by";
    // this build of the agent: files an older build wrote may be laid out differently, and the server re-sends a
    // config only when its hash differs — so they are reported as stale until this build has written them
    private static readonly string Build = typeof(MeshManager).Module.ModuleVersionId.ToString("N");
    private const int DefaultBenchSeconds = 5;
    // The Network page animates traffic from these numbers: a status is one docker exec (four API calls and a
    // shell) with a ping per peer, so it runs every few seconds only while that page is open (the server says, see
    // Pace) and once a minute otherwise
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(60);
    private readonly Pacing _refresh = new(RefreshEvery);
    // how long each dm-mesh command may take
    private static readonly TimeSpan KeygenTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DownTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(15);
    // up to nine pings of a second per peer, the peers side by side
    private static readonly TimeSpan PmtuTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BenchSlack = TimeSpan.FromSeconds(20); // on top of the measured seconds: iperf3 start and connect

    private readonly string _meshDir;
    private readonly MeshSidecar _sidecar;
    private readonly ILogger<MeshManager> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public MeshManager(DockerService docker, IOptions<AgentOptions> opts, ILogger<MeshManager> log)
    {
        _meshDir = Path.Combine(Path.TrimEndingDirectorySeparator(opts.Value.DataDir), "mesh");
        _sidecar = new MeshSidecar(docker, _meshDir, log);
        _log = log;
    }

    private string PathOf(string file) => Path.Combine(_meshDir, file);

    /// <summary>A config was applied at some point: the sidecar is expected to exist and the status is reported.</summary>
    public bool Configured => File.Exists(PathOf(EnvFile)) && File.Exists(PathOf(ConfFile));

    /// <summary>Latest status of dm0, for the heartbeat. Null until joined.</summary>
    public MeshStatus? Cached { get; private set; }

    /// <summary>How often to refresh the status from now on; a faster pace refreshes at once.</summary>
    public void Every(TimeSpan every) => _refresh.Set(every);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // the mesh outlives agent restarts and re-installs: the files are there, make sure the sidecar is too
        try
        {
            await _lock.WaitAsync(stoppingToken);
            try
            {
                if (Configured) await _sidecar.EnsureAsync(null, stoppingToken);
            }
            finally { _lock.Release(); }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (Exception ex) { _log.LogWarning("mesh sidecar: {Message}", ex.Message); }
        while (!stoppingToken.IsCancellationRequested)
        {
            // a join, apply or leave holds the lock and refreshes itself: skip the tick instead of racing it
            // (a refresh during a leave would bring back the status the leave just cleared)
            if (await _lock.WaitAsync(0))
            {
                try
                {
                    if (Configured)
                    {
                        try { await RefreshAsync(stoppingToken); }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                        catch (Exception ex) { _log.LogDebug("mesh status: {Message}", ex.Message); Cached = Problem("status unavailable: " + ex.Message); }
                    }
                    else Cached = null;
                }
                finally { _lock.Release(); }
            }
            try { await _refresh.WaitAsync(stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>The last status, marked down with one problem. A copy: a heartbeat may be serialising <see cref="Cached"/>.</summary>
    private MeshStatus Problem(string text)
    {
        var s = Cached?.Clone() ?? new MeshStatus { PlainRoutes = true };
        s.Up = false;
        s.Problems.Clear();
        s.Problems.Add(text);
        return s;
    }

    /// <summary>Keys + sidecar + host checks. Returns the JSON the server reads: {publicKey, backend, listenPort, hostIps, problems}.</summary>
    public async Task<string> JoinAsync(MeshJoin req, Func<string, Task> progress, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_meshDir);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_meshDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await _sidecar.EnsureAsync(progress, ct);
            var publicKey = (await _sidecar.ExecAsync("keygen", KeygenTimeout, ct)).Trim();
            var problems = new List<string>();
            var firstPort = req.ListenPort > 0 ? req.ListenPort : MeshConfig.DefaultPort;
            var port = firstPort;
            MeshConfig.Probe probe = null!;
            // a port another WireGuard already holds: take the next free one, the server stores it
            for (var attempt = 0; attempt < 10; attempt++, port++)
            {
                probe = MeshConfig.ParseProbe(await _sidecar.ExecAsync($"probe {port} {req.Subnet}", ProbeTimeout, ct));
                if (!probe.PortBusy) break;
                await progress($"UDP port {port} is in use, trying {port + 1}");
            }
            if (!probe.Kernel && !probe.Tun)
                // typical for a Proxmox LXC: the container cannot load kernel modules and has no tun device
                throw new InvalidOperationException("no WireGuard on this host: the kernel module is not loaded and there is no /dev/net/tun for wireguard-go. "
                    + "In a Proxmox LXC run on the Proxmox host: modprobe wireguard && echo wireguard > /etc/modules-load.d/wireguard.conf");
            if (probe.PortBusy) problems.Add($"no free UDP port between {firstPort} and {port - 1}");
            foreach (var o in MeshConfig.Overlaps(req.Subnet, probe)) problems.Add($"mesh subnet {req.Subnet} overlaps {o}");
            await progress($"WireGuard: {(probe.Kernel ? "kernel module" : "wireguard-go (userspace)")}, UDP port {port}");
            return JsonSerializer.Serialize(new
            {
                publicKey,
                backend = probe.Kernel ? "kernel" : "wireguard-go",
                listenPort = port,
                hostIps = probe.HostIps,
                problems,
            });
        }
        finally { _lock.Release(); }
    }

    /// <summary>Writes the config and syncs the interface. Idempotent.</summary>
    public async Task<string> ApplyAsync(MeshApply req, Func<string, Task> progress, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_meshDir);
            await _sidecar.EnsureAsync(progress, ct);
            if (!File.Exists(PathOf(PrivateKeyFile))) await _sidecar.ExecAsync("keygen", KeygenTimeout, ct);
            var privateKey = await File.ReadAllTextAsync(PathOf(PrivateKeyFile), ct);
            var conf = PathOf(ConfFile);
            await File.WriteAllTextAsync(conf, MeshConfig.WgConf(req, privateKey), ct);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(conf, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            // every address reachable from here gets pinged for the status, also the ones behind a hub
            var reachable = req.Peers.SelectMany(p => p.AllowedIps).Select(a => a.Split('/')[0]).Concat(req.Routes.Select(r => r.MeshIp))
                .Distinct().Order(StringComparer.Ordinal);
            await File.WriteAllTextAsync(PathOf(PeersFile), string.Concat(reachable.Select(ip => ip + "\n")), ct);
            await File.WriteAllTextAsync(PathOf(ForwardFile), MeshConfig.ForwardFile(req), ct);
            await File.WriteAllTextAsync(PathOf(RoutesFile), MeshConfig.RoutesFile(req), ct);
            await File.WriteAllTextAsync(PathOf(HaproxyFile), MeshConfig.HaproxyConfig(req), ct);
            await File.WriteAllTextAsync(PathOf(EnvFile), MeshConfig.EnvFile(req), ct);
            await File.WriteAllTextAsync(PathOf(WrittenByFile), Build, ct);
            var output = await _sidecar.ExecAsync("sync", SyncTimeout, ct);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries)) await progress(line.Trim());
            await RefreshAsync(ct);
            return $"mesh {req.Address}: {req.Peers.Count} peer(s)";
        }
        finally { _lock.Release(); }
    }

    /// <summary>Interface down, sidecar and keys gone.</summary>
    public async Task<string> LeaveAsync(Func<string, Task> progress, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (await _sidecar.FindAsync(ct) != null)
            {
                try { await _sidecar.ExecAsync("down", DownTimeout, ct); }
                catch (Exception ex) { await progress("could not take dm0 down cleanly: " + ex.Message); }
                await _sidecar.RemoveAsync(ct);
            }
            if (Directory.Exists(_meshDir)) Directory.Delete(_meshDir, true);
            Cached = null;
            return "left the mesh";
        }
        finally { _lock.Release(); }
    }

    /// <summary>The path MTU to every peer this host sends to, as JSON for the server (see MeshTune in the protocol).</summary>
    public async Task<string> TuneAsync(CancellationToken ct)
    {
        if (!Configured) throw new InvalidOperationException("this server is not in the mesh");
        var paths = MeshConfig.ParsePmtu(await _sidecar.ExecAsync("pmtu", PmtuTimeout, ct));
        return JsonSerializer.Serialize(paths.Select(p => new { publicKey = p.PublicKey, host = p.Host, mtu = p.Mtu }));
    }

    /// <summary>One side of an iperf3 run; the client side returns iperf3's JSON.</summary>
    public async Task<string> BenchAsync(MeshBench req, CancellationToken ct)
    {
        if (!Configured) throw new InvalidOperationException("this server is not in the mesh");
        var seconds = req.Seconds > 0 ? req.Seconds : DefaultBenchSeconds;
        var timeout = TimeSpan.FromSeconds(seconds) + BenchSlack;
        var ip = req.Ip.Trim();
        if (ip.Length == 0 || ip.Any(c => !char.IsAsciiDigit(c) && c != '.')) throw new ArgumentException("bad ip");
        return req.Server
            ? await _sidecar.ExecAsync($"bench-server {ip}", timeout, ct)
            : await _sidecar.ExecAsync($"bench-client {ip} {seconds}{(req.Reverse ? " -R" : "")}", timeout, ct);
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var map = MeshConfig.PeerMap(await File.ReadAllTextAsync(PathOf(ConfFile), ct));
        var text = await _sidecar.ExecAsync("status", StatusTimeout, ct);
        var s = MeshConfig.ParseStatus(text, k => map.GetValueOrDefault(k));
        var env = await File.ReadAllTextAsync(PathOf(EnvFile), ct);
        var writtenBy = File.Exists(PathOf(WrittenByFile)) ? (await File.ReadAllTextAsync(PathOf(WrittenByFile), ct)).Trim() : "";
        s.ConfigHash = writtenBy == Build ? MeshConfig.EnvValue(env, "HASH") : "";
        s.Address = MeshConfig.EnvValue(env, "ADDRESS");
        if (s.ListenPort == 0 && int.TryParse(MeshConfig.EnvValue(env, "PORT"), out var port)) s.ListenPort = port;
        if (s.PublicKey.Length == 0 && File.Exists(PathOf(PublicKeyFile))) s.PublicKey = (await File.ReadAllTextAsync(PathOf(PublicKeyFile), ct)).Trim();
        if (!s.Up) s.Problems.Add("dm0 is down");
        s.PlainRoutes = true;
        Cached = s;
    }
}
