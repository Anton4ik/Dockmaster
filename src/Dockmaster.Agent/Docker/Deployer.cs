using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Dockmaster.Protocol;

namespace Dockmaster.Agent.Docker;

/// <summary>
/// Deploys a compose project service by service.
///
/// Rolling update (no container_name, no published ports, replicas > 1 or x-dockmaster.rolling: true):
/// replicas are replaced in batches of deploy.update_config.parallelism (default 1). For each batch a new
/// replica is started next to the old one, we wait until it is READY, run the optional pre-stop hook in the
/// old one (drain), stop and remove it, then pause deploy.update_config.delay before the next batch.
/// A new replica takes over the slot of the one it replaces: it starts as project-service-N with compose number N,
/// while the old one steps aside as …-old, so names and numbers stay 1..N across rollouts.
///
/// READY = healthcheck reports healthy when the service has one; otherwise the container stayed running
/// (no restart) for deploy.update_config.monitor (default 10s).
///
/// Everything else is recreated in place and waited on with the same readiness rule.
/// </summary>
public sealed partial class Deployer
{
    private readonly ComposeRunner _compose;
    private readonly DockerService _docker;
    private readonly ImageStore _images;
    private readonly ILogger<Deployer> _log;
    private readonly ProjectLocks _locks;
    private readonly string _dataDir;

    public Deployer(ComposeRunner compose, DockerService docker, ImageStore images, ProjectLocks locks, IOptions<AgentOptions> opts, ILogger<Deployer> log)
    {
        _compose = compose;
        _docker = docker;
        _images = images;
        _locks = locks;
        _dataDir = Path.TrimEndingDirectorySeparator(opts.Value.DataDir);
        _log = log;
    }

    public async Task<(bool ok, string message)> DeployAsync(DeployProject req, Func<string, Task> log, CancellationToken ct)
    {
        _log.LogInformation("deploy {Project}", req.Project);
        using var _ = await _locks.EnterAsync(req.Project, log, ct);
        var timeout = req.HealthTimeoutSeconds > 0 ? TimeSpan.FromSeconds(req.HealthTimeoutSeconds) : Readiness.Timeout;
        if (await _compose.LoginAllAsync(req.Registries, log, ct) is { } loginError) return (false, loginError);

        if (req.Secrets.Count + req.Files.Count > 0 && await _docker.DataDirProblemAsync(_dataDir, ct) is { } problem) await log("⚠ " + problem);
        string[] baseArgs;
        try { baseArgs = _compose.PrepareProject(req.Project, req.ComposeYaml, req.EnvFile, req.Secrets, req.Files); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await log(ex.Message);
            return (false, "could not write the project's files");
        }
        JsonObject cfg;
        try { cfg = await _compose.ConfigAsync(baseArgs, ct); }
        catch (Exception ex) { await log(ex.Message); return (false, "compose config is invalid"); }

        var services = cfg["services"]?.AsObject() ?? new JsonObject();
        var wanted = req.Services.Count > 0 ? req.Services.ToList() : services.Select(s => s.Key).ToList();
        var missing = wanted.Where(s => !services.ContainsKey(s)).ToList();
        if (missing.Count > 0) return (false, "unknown services: " + string.Join(", ", missing));

        if (req.Pull)
        {
            await log($"pull {string.Join(' ', wanted)}");
            // images that are built locally (build: sections) have nothing to pull
            var code = await _compose.RunAsync([.. baseArgs, "pull", "-q", "--ignore-buildable", .. wanted], log, ct);
            _docker.ForgetImages();
            if (code != 0) return (false, "image pull failed");
        }

        // x-dockmaster: {parallel: N} at the top of the compose file: up to N services at a time (default 1).
        // Either way a service waits for the services it depends_on, so they are updated first.
        var exts = ComposeSpec.ServiceExtensions(req.ComposeYaml);
        var parallel = Math.Clamp(ComposeSpec.ParseInt(cfg["x-dockmaster"]?["parallel"]) ?? 1, 1, 8);
        var deps = wanted.ToDictionary(s => s, s => ComposeSpec.DependsOn(services[s]!.AsObject()).Where(d => d != s && wanted.Contains(d)).ToList());
        if (parallel > 1) await log($"up to {parallel} services at a time");
        var gate = new SemaphoreSlim(parallel);
        var failed = new List<string>();
        var started = new Dictionary<string, Task>();
        // compose config rejects dependency cycles; all tasks are created in this synchronous first pass
        Task Deploy(string svc) => started.TryGetValue(svc, out var t) ? t : started[svc] = DeployServiceAsync(svc);
        async Task DeployServiceAsync(string svc)
        {
            await Task.WhenAll(deps[svc].Select(Deploy));
            await gate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                // side by side the lines of several services interleave: tag them
                Func<string, Task> slog = parallel > 1 ? line => log($"[{svc}] {line.TrimStart()}") : log;
                var plan = ComposeSpec.Plan(svc, services[svc]!.AsObject(), timeout, exts.GetValueOrDefault(svc));
                var ready = plan.HasHealthcheck ? "healthcheck" : $"running {plan.Monitor.TotalSeconds:0}s";
                await slog(plan.Job ? $"=== {svc}: a job on a schedule ({exts.GetValueOrDefault(svc)?["schedule"]}) — created, not started"
                    : $"=== {svc}: {(plan.Rolling ? $"rolling update, {plan.Parallelism} at a time" : "recreate")}, replicas={plan.Replicas}, ready = {ready}{(plan.Delay > TimeSpan.Zero ? $", delay {plan.Delay.TotalSeconds:0}s" : "")}{(plan.DrainUntilUnhealthy ? ", drain until unhealthy" : "")}");
                try
                {
                    var image = services[svc]!["image"]?.GetValue<string>();
                    // only the replica count changed: replicas are added or removed, the running ones stay
                    var scaled = plan.Scalable && !req.ForceRecreate ? await ScaleAsync(baseArgs, req.Project, plan, image, slog, ct) : null;
                    var ok = scaled ?? (plan.Rolling
                        ? await RolloutAsync(baseArgs, req.Project, plan, image, req.ForceRecreate, slog, ct)
                        : await RecreateAsync(baseArgs, plan, req.ForceRecreate, slog, ct));
                    if (!ok) lock (failed) failed.Add(svc);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    await slog($"  ✗ {svc}: {ex.Message}");
                    lock (failed) failed.Add(svc);
                }
            }
            finally { gate.Release(); }
        }
        await Task.WhenAll(wanted.Select(Deploy).ToList());
        if (req.Pull && failed.Count == 0) await _images.CleanUpAfterPullAsync(log, ct);
        return failed.Count == 0 ? (true, "deployed: " + string.Join(", ", wanted)) : (false, "failed: " + string.Join(", ", failed));
    }

    private async Task<bool> RecreateAsync(string[] baseArgs, ServicePlan plan, bool force, Func<string, Task> log, CancellationToken ct)
    {
        if (plan.Job) return await CreateJobAsync(baseArgs, plan, force, log, ct);
        var args = new List<string>(baseArgs) { "up", "-d", "--no-deps", "--remove-orphans" };
        if (force) args.Add("--force-recreate");
        args.Add(plan.Name);
        // compose stops the old container before it creates the new one: killing it in between leaves the service
        // down (and for the Dockmaster container itself the cancel arrives exactly then), so a started up finishes
        ct.ThrowIfCancellationRequested();
        var code = await _compose.RunAsync(args, l => log("  " + l), CancellationToken.None);
        if (code != 0) { await log($"  ✗ compose up failed ({code})"); return false; }
        var ids = await _compose.ContainerIdsAsync(baseArgs, plan.Name, false, ct);
        var ok = await WaitReadyAsync(ids, plan, log, ct);
        await PrintStatusAsync(ids, log, ct);
        return ok;
    }

    /// <summary>
    /// A job's container is created (or re-created when its image or settings changed) but not started: the server
    /// starts it when the schedule says so (<c>compose start</c>), and the app exits when it is done. A run going on
    /// with the old image or settings is stopped by the re-creation, as any service's would be. Nothing to wait for.
    /// </summary>
    private async Task<bool> CreateJobAsync(string[] baseArgs, ServicePlan plan, bool force, Func<string, Task> log, CancellationToken ct)
    {
        var args = new List<string>(baseArgs) { "up", "--no-start", "--no-deps", "--remove-orphans" };
        if (force) args.Add("--force-recreate");
        args.Add(plan.Name);
        ct.ThrowIfCancellationRequested();
        var code = await _compose.RunAsync(args, l => log("  " + l), CancellationToken.None);
        if (code != 0) { await log($"  ✗ compose up --no-start failed ({code})"); return false; }
        var ids = await _compose.ContainerIdsAsync(baseArgs, plan.Name, false, ct);
        foreach (var id in ids)
        {
            try
            {
                var i = await _docker.InspectAsync(id, ct);
                var name = i.Name.TrimStart('/');
                await log(i.State.Status == "running" ? $"  ✓ {name}  running (a run going on, unchanged)" : $"  ✓ {name}  {i.State.Status} — waits for its schedule");
                if (DockerService.RestartPolicyName(i.HostConfig.RestartPolicy) is not ("no" or "on-failure"))
                    await log($"  ⚠ {name} has restart: {DockerService.RestartPolicyName(i.HostConfig.RestartPolicy)} — docker would start it again as soon as it exits; set it to \"no\"");
            }
            catch (Exception ex) { await log($"  ? {id[..12]}: {ex.Message}"); }
        }
        return true;
    }

    /// <summary>The compose call of a start / stop / restart / down; the services after "--", never read as flags of it.</summary>
    internal static List<string> ActionArgs(IEnumerable<string> baseArgs, ProjectOp op, IReadOnlyCollection<string> services)
    {
        var verb = op switch
        {
            ProjectOp.ProjectStart => "start",
            ProjectOp.ProjectStop => "stop",
            ProjectOp.ProjectRestart => "restart",
            ProjectOp.ProjectDown => "down",
            _ => throw new ArgumentOutOfRangeException(nameof(op))
        };
        var args = new List<string>(baseArgs) { verb };
        if (op == ProjectOp.ProjectDown) args.Add("--remove-orphans");
        if (services.Count > 0) args.Add("--");
        args.AddRange(services);
        return args;
    }

    public async Task<(bool ok, string message)> ProjectActionAsync(ProjectAction req, Func<string, Task> log, CancellationToken ct)
    {
        using var _ = await _locks.EnterAsync(req.Project, log, ct);
        var baseArgs = _compose.ExistingProjectArgs(req.Project);
        if (baseArgs == null) return (false, "project files not found on this agent (deploy it first)");
        if (req.Services.FirstOrDefault(s => s.Length == 0 || s[0] == '-') is { } bad) return (false, $"'{bad}' is not a service name");
        if (await StartDirectAsync(req, log, ct) is { } started) return started;
        var args = ActionArgs(baseArgs, req.Op, req.Services);
        var verb = args[baseArgs.Length];
        // like up: a stop / down / restart killed half way leaves the project in between
        ct.ThrowIfCancellationRequested();
        var code = await _compose.RunAsync(args, log, CancellationToken.None);
        // a partial down keeps the files: the other services still need their compose file, .env and secrets.
        // Only Dockmaster's own files go: `./data` bind mounts live in the same folder and are the app's data.
        if (code == 0 && req.Op == ProjectOp.ProjectDown && req.Services.Count == 0)
        {
            try { if (_compose.RemoveProjectFiles(req.Project) is { } kept) await log(kept); }
            catch (Exception ex) { await log($"could not remove the project files: {ex.Message}"); }
        }
        return code == 0 ? (true, $"{verb} ok") : (false, $"compose {verb} failed ({code})");
    }
}
