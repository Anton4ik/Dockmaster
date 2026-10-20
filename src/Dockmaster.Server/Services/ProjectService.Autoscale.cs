using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Protocol;
using Dockmaster.Server.Data;

namespace Dockmaster.Server.Services;

/// <summary>What an autoscaler change came to; <c>Busy</c>: nothing done, a deploy or another run of the project was going on.</summary>
public sealed record AutoscaleOutcome(bool Ok, string Message, bool Busy = false);

public sealed partial class ProjectService
{
    // runs going on per project: the autoscaler leaves a project alone meanwhile
    private readonly ConcurrentDictionary<int, int> _running = new();
    // one plan-and-run of a project at a time on this server: a deploy planned without the autoscaler's latest
    // replicas must not undo them on an agent after it
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _projectLocks = new();

    /// <summary>A deploy or another run of this project is going on.</summary>
    public bool IsRunning(int projectId) => _running.GetValueOrDefault(projectId) > 0;

    private async Task<IDisposable?> LockAsync(int projectId, bool wait, CancellationToken ct)
    {
        var sem = _projectLocks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        if (wait) await sem.WaitAsync(ct);
        else if (!await sem.WaitAsync(0, ct)) return null;
        return new Released(sem);
    }

    private sealed class Released(SemaphoreSlim sem) : IDisposable
    {
        private SemaphoreSlim? _sem = sem;
        public void Dispose() => Interlocked.Exchange(ref _sem, null)?.Release();
    }

    /// <summary>
    /// Sets the replicas the autoscaler runs of a service beyond deploy.replicas (agent id → count) and applies them:
    /// the service is deployed where its share grew or shrank — the running replicas stay, only the difference starts
    /// or stops — and taken down where it has none left. A deploy that fails puts the previous count back. Refused
    /// while the service has changes in the compose file that were never deployed: a new replica would bring them out
    /// on one server only.
    /// </summary>
    public async Task<AutoscaleOutcome> AutoscaleAsync(int projectId, string service, IReadOnlyDictionary<int, int> extra, string label, CancellationToken ct)
    {
        using var held = await LockAsync(projectId, false, ct);
        if (held == null) return new AutoscaleOutcome(false, "a deploy of the project is going on", Busy: true);

        ProjectPlan before;
        DeployInputs inputs;
        Dictionary<string, IReadOnlyDictionary<int, int>> previous;
        await using (var db = await _db.CreateDbContextAsync(ct))
        {
            before = await LoadPlanAsync(db, projectId, ct);
            inputs = await LoadInputsAsync(db, projectId, ct);
            previous = before.Project.Extras;
        }
        int Share(ProjectPlan plan, int agentId) => plan.Split.Hosts.FirstOrDefault(h => h.AgentId == agentId)?.Replicas.GetValueOrDefault(service) ?? 0;
        var growing = extra.Any(x => x.Value > (previous.GetValueOrDefault(service)?.GetValueOrDefault(x.Key) ?? 0));
        if (growing && ServiceFingerprint(before.Project, service) is { } now && before.Project.DeployedFingerprints.GetValueOrDefault(service) != now)
            return new AutoscaleOutcome(false, $"the compose file has changes of {service} that were not deployed — deploy it first");

        var next = new Dictionary<string, IReadOnlyDictionary<int, int>>(previous);
        if (extra.Values.Any(n => n > 0)) next[service] = extra.Where(x => x.Value > 0).ToDictionary();
        else next.Remove(service);
        await SaveExtrasAsync(projectId, next);
        var after = await PlanAsync(projectId, ct);

        var steps = new List<Step>();
        var deployTo = after.Split.Hosts.Where(h => h.Replicas.GetValueOrDefault(service) > 0 && h.Replicas[service] != Share(before, h.AgentId)).ToList();
        foreach (var host in deployTo) steps.Add(DeployStep(after.Project, host, [service], false, false, inputs, _ => { }));
        // taken down where it has no replica left: where the plan had it, and where the autoscaler had put one (the
        // plan no longer shows those once autoscaling is switched off)
        var had = before.Split.Hosts.Where(h => h.Replicas.GetValueOrDefault(service) > 0).Select(h => h.AgentId)
            .Concat(previous.GetValueOrDefault(service)?.Keys ?? []).Distinct();
        foreach (var id in had.Where(id => Share(after, id) == 0 && after.AgentNames.ContainsKey(id)))
            steps.Add(new Step(id, after.AgentNames[id], "remove " + service,
                new Command { ProjectAction = new ProjectAction { Project = after.Project.Name, Op = ProjectOp.ProjectDown, Services = { service } } }));
        if (steps.Count == 0) return new AutoscaleOutcome(true, "nothing to change");

        // what stops a deploy stops a new replica — but only on the servers that get one; taking one away always goes
        var offlineElsewhere = after.Split.Hosts.Where(h => !deployTo.Contains(h)).Select(h => $"server {h.AgentName} is offline").ToHashSet();
        if (deployTo.Count > 0 && after.Split.Problems.FirstOrDefault(p => !offlineElsewhere.Contains(p)) is { } problem)
        {
            await SaveExtrasAsync(projectId, previous);
            return new AutoscaleOutcome(false, problem);
        }

        var (ok, message, _) = await RunAsync(after.Project, label, steps, _ => { }, ct);
        if (!ok && deployTo.Count > 0) await SaveExtrasAsync(projectId, previous);
        await SaveDeployedAsync(projectId, after.Project.DeployedIds.Concat(after.AgentIds).Distinct());
        return new AutoscaleOutcome(ok, message);
    }

    private async Task SaveExtrasAsync(int projectId, Dictionary<string, IReadOnlyDictionary<int, int>> extras)
    {
        await using var db = await _db.CreateDbContextAsync(CancellationToken.None);
        if (await db.Projects.FindAsync([projectId]) is not { } p) return;
        p.Extras = extras;
        await db.SaveChangesAsync();
    }

    /// <summary>The service as the compose file and .env say now, without what a new replica count does not change.</summary>
    public static string? ServiceFingerprint(ProjectEntity p, string service) => ComposeServices.Fingerprint(p.ComposeYaml, service, p.EnvFile);

    /// <summary>After a deploy that went through: what the deployed services looked like (as the deploy read the project).</summary>
    private async Task SaveFingerprintsAsync(ProjectEntity deployed, IEnumerable<string>? services)
    {
        await using var db = await _db.CreateDbContextAsync(CancellationToken.None);
        if (await db.Projects.FindAsync([deployed.Id]) is not { } p) return;
        var prints = p.DeployedFingerprints;
        foreach (var s in services ?? ComposeServices.Parse(deployed.ComposeYaml).Select(x => x.Name))
            if (ServiceFingerprint(deployed, s) is { } f) prints[s] = f;
        p.DeployedFingerprints = prints;
        await db.SaveChangesAsync();
    }
}
