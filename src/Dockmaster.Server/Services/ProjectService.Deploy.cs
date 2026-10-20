using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Protocol;
using Dockmaster.Server.Data;

namespace Dockmaster.Server.Services;

public sealed partial class ProjectService
{
    private sealed record Step(int AgentId, string AgentName, string Label, Command Command);

    /// <summary>What a deploy sends besides the compose file: the registries' logins, the project's secrets and files.</summary>
    private sealed record DeployInputs(List<RegistryEntity> Registries, List<ProjectSecretEntity> Secrets, Dictionary<string, string> Files);

    /// <summary>How many runs of a project the history keeps: each holds its log, and a deploy its compose file and .env.</summary>
    public const int HistoryKept = 100;

    /// <param name="source">who started it, for the history: the signed-in user ("anna"), "webhook", an API key</param>
    /// <param name="started">called once the deploy has its turn (after the one going on), before it reads the project</param>
    public async Task<(bool ok, string message)> DeployAsync(int projectId, IEnumerable<string>? services, bool pull, bool forceRecreate, Action<string> onLine, CancellationToken ct,
        string source = "", Action? started = null)
    {
        using var held = await LockAsync(projectId, true, ct);
        started?.Invoke();
        return await DeployHeldAsync(projectId, services, pull, forceRecreate, onLine, ct, source);
    }

    /// <summary>A deploy for a caller that holds the project's lock already.</summary>
    private async Task<(bool ok, string message)> DeployHeldAsync(int projectId, IEnumerable<string>? services, bool pull, bool forceRecreate, Action<string> onLine, CancellationToken ct,
        string source)
    {
        ProjectPlan plan;
        DeployInputs inputs;
        await using (var db = await _db.CreateDbContextAsync(ct))
        {
            plan = await LoadPlanAsync(db, projectId, ct);
            inputs = await LoadInputsAsync(db, projectId, ct);
        }
        var project = plan.Project;
        if (plan.Split.Problems.Count > 0)
        {
            foreach (var p in plan.Split.Problems) onLine("✗ " + p);
            return (false, plan.Split.Problems[0]);
        }
        foreach (var w in plan.Split.Warnings) onLine("⚠ " + w);
        // an older agent leaves a ${SECRET} empty: the service would start without its password
        foreach (var host in plan.Split.Hosts)
        {
            if (_agents.Get(host.AgentId) is not { SecretVariables: false }) continue;
            var used = SecretVariablesUsed(host.ComposeYaml, inputs.Secrets.Select(s => s.Name), project.EnvFile ?? "");
            if (used.Count == 0) continue;
            var problem = $"{host.AgentName} runs an older agent that does not fill in secrets used as variables ({string.Join(", ", used.Take(3))}{(used.Count > 3 ? $" and {used.Count - 3} more" : "")}): update the agent first";
            onLine("✗ " + problem);
            return (false, problem);
        }

        var wanted = services?.ToHashSet();
        var steps = new List<Step>();
        // servers with exposed services first: the ones that use them come up against a running provider
        foreach (var host in plan.Split.Hosts.OrderByDescending(h => h.Exposed.Count > 0))
        {
            var hostServices = ServicesOn(host, plan, wanted);
            if (hostServices is { Count: 0 }) continue;
            steps.Add(DeployStep(project, host, hostServices, pull, forceRecreate, inputs, onLine));
        }
        // a service moved away from a server it was deployed to: nothing of the project is left there, take it down
        var stale = wanted == null ? project.DeployedIds.Except(plan.AgentIds).Where(plan.AgentNames.ContainsKey).ToList() : [];
        foreach (var id in stale)
            steps.Add(new Step(id, plan.AgentNames[id], "remove", new Command { ProjectAction = new ProjectAction { Project = project.Name, Op = ProjectOp.ProjectDown } }));

        var action = services == null ? "deploy" : "deploy " + string.Join(",", services);
        var (ok, message, outcomes) = await RunAsync(project, action, steps, onLine, ct, source, snapshot: true);

        // remember where the project lives now (a server whose removal failed stays on the list, so it is retried);
        // a deploy of some services leaves the others where they are, so no server is forgotten
        var where = plan.AgentIds.Concat(stale.Where(id => !outcomes.GetValueOrDefault(id, false)));
        if (wanted != null) where = where.Concat(project.DeployedIds);
        await SaveDeployedAsync(project.Id, where.Distinct());
        if (ok) await SaveFingerprintsAsync(project, wanted);
        if (ok && pull)
            foreach (var host in plan.Split.Hosts)
            {
                var images = _agents.Get(host.AgentId)?.Last?.Containers
                    .Where(c => c.Project == project.Name && (wanted == null || wanted.Contains(c.Service))).Select(c => c.ImageRef) ?? [];
                _updates.RecheckInBackground(host.AgentId, images);
            }
        return (ok, message);
    }

    private static async Task<DeployInputs> LoadInputsAsync(DockmasterDb db, int projectId, CancellationToken ct) => new(
        await db.Registries.ToListAsync(ct),
        await db.ProjectSecrets.Where(s => s.ProjectId == projectId).ToListAsync(ct),
        await db.ProjectFiles.Where(f => f.ProjectId == projectId).ToDictionaryAsync(f => f.Name, f => f.Content, ct));

    /// <param name="services">null = all of the host's services</param>
    /// <summary>The secrets a compose file uses as ${VARIABLES} that the .env does not set as well.</summary>
    public static List<string> SecretVariablesUsed(string composeYaml, IEnumerable<string> secrets, string envFile)
    {
        var inEnv = envFile.Split('\n').Select(l => l.Split('=', 2)[0].Trim().Replace("export ", "")).ToHashSet();
        return secrets.Where(n => !inEnv.Contains(n) && Regex.IsMatch(composeYaml, @"\$\{" + Regex.Escape(n) + @"([:?+-][^}]*)?\}|\$" + Regex.Escape(n) + @"\b"))
            .Order(StringComparer.Ordinal).ToList();
    }

    private Step DeployStep(ProjectEntity project, HostPlan host, List<string>? services, bool pull, bool forceRecreate, DeployInputs inputs, Action<string> onLine)
    {
        var (regs, secrets, files) = inputs;
        var cmd = new DeployProject
        {
            Project = project.Name,
            // a service that mounts a project file is re-created when the file changes
            ComposeYaml = files.Count > 0 ? ComposeFiles.Stamp(host.ComposeYaml, project.Name, files, w => onLine("⚠ " + w)) : host.ComposeYaml,
            EnvFile = project.EnvFile ?? "",
            Pull = pull,
            ForceRecreate = forceRecreate,
            HealthTimeoutSeconds = project.HealthTimeoutSeconds,
        };
        if (services != null) cmd.Services.AddRange(services);
        foreach (var s in secrets) cmd.Secrets[s.Name] = _secrets.Unprotect(s.ValueProtected);
        cmd.Files.Add(files);
        // only send credentials for registries the compose file actually references
        cmd.Registries.AddRange(ImageRef.Credentials(ComposeServices.ImageRefs(host.ComposeYaml), regs, _registries));
        return new Step(host.AgentId, host.AgentName, "deploy", new Command { Deploy = cmd });
    }

    /// <summary>"anna: rollback to #12": what started a run, with the user who did it ("" = nobody signed in, a webhook).</summary>
    public static string By(string user, string what) => user.Length == 0 ? what : what.Length == 0 ? user : $"{user}: {what}";

    /// <param name="source">who started it, for the history: the signed-in user ("anna"), an API key</param>
    public async Task<(bool ok, string message)> ActionAsync(int projectId, ProjectOp op, IEnumerable<string>? services, Action<string> onLine, CancellationToken ct,
        string source = "")
    {
        using var held = await LockAsync(projectId, true, ct);
        return await ActionHeldAsync(projectId, op, services, onLine, ct, source);
    }

    /// <summary>
    /// Takes the project down and forgets it, holding its lock throughout: a run queued behind the "down" finds the
    /// project gone instead of bringing its containers back. A project whose servers cannot take it down is kept, unless forced.
    /// </summary>
    public async Task<(bool down, bool deleted, string message)> TakeDownAndDeleteAsync(int projectId, bool force, CancellationToken ct, string source = "")
    {
        using var held = await LockAsync(projectId, true, ct);
        var (ok, message) = await ActionHeldAsync(projectId, ProjectOp.ProjectDown, null, _ => { }, ct, source);
        if (!ok && !force) return (false, false, message);
        await using var db = await _db.CreateDbContextAsync(CancellationToken.None);
        await DeleteRowsAsync(db, projectId, CancellationToken.None);
        return (ok, true, message);
    }

    /// <summary>The project's rows: itself, its history, secrets and files, in one transaction.</summary>
    public static async Task DeleteRowsAsync(DockmasterDb db, int projectId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Projects.Where(p => p.Id == projectId).ExecuteDeleteAsync(ct);
        await db.Deployments.Where(d => d.ProjectId == projectId).ExecuteDeleteAsync(ct);
        await db.ProjectSecrets.Where(s => s.ProjectId == projectId).ExecuteDeleteAsync(ct);
        await db.ProjectFiles.Where(f => f.ProjectId == projectId).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task<(bool ok, string message)> ActionHeldAsync(int projectId, ProjectOp op, IEnumerable<string>? services, Action<string> onLine, CancellationToken ct,
        string source)
    {
        var wanted = services?.ToHashSet();
        // the names go to compose as arguments: "--volumes" would be a flag of down and delete the project's volumes
        if (wanted?.FirstOrDefault(s => !ServiceNameRegex().IsMatch(s)) is { } bad) return (false, $"'{bad}' is not a service name");
        var plan = await PlanAsync(projectId, ct);
        var project = plan.Project;
        var label = op.ToString().Replace("Project", "").ToLowerInvariant();
        var steps = new List<Step>();
        // the plan says where the services are now; "down" also reaches servers the project was deployed to before
        var hostIds = plan.AgentIds.ToList();
        if (op == ProjectOp.ProjectDown && wanted == null) hostIds.AddRange(project.DeployedIds.Except(hostIds).Where(plan.AgentNames.ContainsKey));
        foreach (var id in hostIds)
        {
            var hostServices = ServicesOn(plan.Split.Hosts.FirstOrDefault(h => h.AgentId == id), plan, wanted);
            if (hostServices is { Count: 0 }) continue;
            var pa = new ProjectAction { Project = project.Name, Op = op };
            if (hostServices != null) pa.Services.AddRange(hostServices);
            steps.Add(new Step(id, plan.AgentNames.GetValueOrDefault(id, "?"), label, new Command { ProjectAction = pa }));
        }
        var (ok, message, outcomes) = await RunAsync(project, label, steps, onLine, ct, source);
        if (op == ProjectOp.ProjectDown && wanted == null)
            await SaveDeployedAsync(project.Id, project.DeployedIds.Where(id => !outcomes.GetValueOrDefault(id, false)));
        return (ok, message);
    }

    /// <summary>
    /// The wanted services that run on this host, null = all of them. A project on one server hands the list to compose
    /// as it is. A service with a balancer takes it along.
    /// </summary>
    private static List<string>? ServicesOn(HostPlan? host, ProjectPlan plan, HashSet<string>? wanted) => PortBalancer.WithBalancers(
        plan.Split.Spanning && wanted != null && host != null ? host.Services.Where(wanted.Contains).ToList() : wanted?.ToList(), host?.ComposeYaml ?? plan.Project.ComposeYaml);

    /// <summary>Runs the steps one after another (a failure does not stop the rest), one deployment record for all of them.</summary>
    /// <param name="snapshot">keep the compose file and .env with the record: a deploy, which can be gone back to</param>
    private async Task<(bool ok, string message, Dictionary<int, bool> outcomes)> RunAsync(ProjectEntity project, string action, List<Step> steps, Action<string> onLine, CancellationToken ct,
        string source = "", bool snapshot = false)
    {
        _running.AddOrUpdate(project.Id, 1, (_, n) => n + 1);
        try { return await RunStepsAsync(project, action, steps, onLine, ct, source, snapshot); }
        finally { _running.AddOrUpdate(project.Id, 0, (_, n) => Math.Max(0, n - 1)); }
    }

    private async Task<(bool ok, string message, Dictionary<int, bool> outcomes)> RunStepsAsync(ProjectEntity project, string action, List<Step> steps, Action<string> onLine, CancellationToken ct,
        string source, bool snapshot)
    {
        var dep = new DeploymentEntity
        {
            ProjectId = project.Id, Action = action, Source = source,
            ComposeYaml = snapshot ? project.ComposeYaml : "", EnvFile = snapshot ? project.EnvFile : "",
        };
        await using (var db = await _db.CreateDbContextAsync(ct))
        {
            db.Deployments.Add(dep);
            await db.SaveChangesAsync(ct);
            await PruneHistoryAsync(db, project.Id, ct);
        }
        // the log goes to the stream that started the run and to anyone who picks it up later (RunLogs)
        var log = new StringBuilder();
        _runLogs.Start(dep.Id);
        void Line(string l)
        {
            log.AppendLine(l);
            _runLogs.Line(dep.Id, l);
            onLine(l);
        }
        try
        {
            var outcomes = new Dictionary<int, bool>();
            var failures = new List<string>();
            var several = steps.Count > 1;
            foreach (var step in steps)
            {
                var prefix = several ? $"[{step.AgentName}] " : "";
                Line($"[{DateTime.Now:HH:mm:ss}] {prefix}{step.Label} → {step.AgentName}");
                var r = await _agents.SendAsync(step.AgentId, step.Command, onProgress: l => Line(prefix + l), ct: ct);
                Line($"[{DateTime.Now:HH:mm:ss}] {prefix}{(r.Ok ? "OK" : "FAILED")}: {r.Message}");
                outcomes[step.AgentId] = r.Ok;
                if (!r.Ok) failures.Add(several ? $"{step.AgentName}: {AgentRegistry.ErrorText(r)}" : AgentRegistry.ErrorText(r));
            }
            var ok = steps.Count > 0 && failures.Count == 0;
            var message = steps.Count == 0 ? "nothing to do on any server"
                : !several ? (ok ? LastResult(log) : failures[0])
                : ok ? $"{action} done on {string.Join(", ", steps.Select(s => s.AgentName).Distinct())}"
                : string.Join("; ", failures);
            await using (var db = await _db.CreateDbContextAsync(CancellationToken.None))
            {
                var d = await db.Deployments.FindAsync([dep.Id]);
                if (d != null)
                {
                    d.FinishedAt = DateTime.UtcNow;
                    d.Ok = ok;
                    d.Message = message;
                    d.Log = log.ToString();
                }
                var p = await db.Projects.FindAsync([project.Id]);
                if (p != null && action.StartsWith("deploy"))
                {
                    p.LastDeployedAt = DateTime.UtcNow;
                    p.LastDeployOk = ok;
                }
                await db.SaveChangesAsync();
            }
            _runLogs.End(dep.Id, ok, message);
            // a run the person cancelled is no failure to report
            if (!ok && !ct.IsCancellationRequested && message != AgentRegistry.CancelledMessage)
                await _notifier.RaiseAsync(new DmEvent(DmEvent.Error, "deploy", $"{project.Name}: {action} failed — {message}", ProjectId: project.Id));
            return (ok, message, outcomes);
        }
        catch (Exception ex)
        {
            // whoever follows the log must not wait for an end that never comes
            _runLogs.End(dep.Id, false, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Keeps the newest <see cref="HistoryKept"/> runs of a project. Deploys (the ones with files to go back to) count
    /// apart from the rest: an autoscaler that adds and removes replicas all day does not push them out.
    /// </summary>
    public static async Task PruneHistoryAsync(DockmasterDb db, int projectId, CancellationToken ct)
    {
        foreach (var withFiles in new[] { true, false })
        {
            var runs = db.Deployments.Where(d => d.ProjectId == projectId && (d.ComposeYaml != "") == withFiles);
            var newestGone = await runs.OrderByDescending(d => d.Id).Skip(HistoryKept).Select(d => (int?)d.Id).FirstOrDefaultAsync(ct);
            if (newestGone is { } id) await runs.Where(d => d.Id <= id).ExecuteDeleteAsync(ct);
        }
    }

    /// <summary>Remembers the servers the project is on now, also when the caller has gone away meanwhile.</summary>
    private async Task SaveDeployedAsync(int projectId, IEnumerable<int> agentIds)
    {
        await using var db = await _db.CreateDbContextAsync(CancellationToken.None);
        if (await db.Projects.FindAsync([projectId]) is not { } p) return;
        p.DeployedAgentIds = string.Join(",", agentIds);
        await db.SaveChangesAsync();
    }

    // the agent's own summary ("deployed: web, db") from the last OK line of a single-step run
    private static string LastResult(StringBuilder log)
    {
        var lines = log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var last = lines.LastOrDefault(l => l.Contains("] OK: ")) ?? "";
        var i = last.IndexOf("] OK: ");
        return i >= 0 ? last[(i + 6)..].Trim() : "done";
    }
}
