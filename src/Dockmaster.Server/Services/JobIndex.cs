using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Protocol;
using Dockmaster.Server.Data;

namespace Dockmaster.Server.Services;

/// <summary>A service with a schedule, where its containers may be.</summary>
/// <param name="AgentIds">the servers the project's containers are on (its placement, and where it was deployed before)</param>
public sealed record Job(int ProjectId, string Project, string Service, ScheduleSpec Spec, IReadOnlySet<int> AgentIds);

/// <summary>A job's last run, in short, for the rows that show a job's container.</summary>
public sealed record JobRunBrief(DateTime StartedAt, DateTime? FinishedAt, bool? Ok, int? ExitCode, string Note);

/// <summary>What a container of a job shows: its schedule in words, the next time, how its last run went.</summary>
public sealed record JobInfoDto(string Schedule, string Cron, string? TimeZone, DateTime? NextAt, JobRunBrief? LastRun);

/// <summary>
/// Which containers are jobs: the services with <c>x-dockmaster: {schedule: …}</c> of every project, by compose
/// project and service name on each server — once they are deployed as jobs. A container that docker still restarts
/// by itself (restart: always / unless-stopped) is the service as it ran before its schedule was saved: it is left
/// alone (not started, not timed out, not counted as a job) until a deploy re-creates it as a job. Read by whoever
/// turns a heartbeat's containers into what a page shows, kept fresh by the <see cref="JobScheduler"/> every minute
/// and by the API when a schedule changes.
/// </summary>
public sealed class JobIndex(IDbContextFactory<DockmasterDb> dbf)
{
    private volatile Dictionary<(int AgentId, string Project, string Service), Job> _byContainer = new();
    private volatile List<Job> _all = [];
    private readonly ConcurrentDictionary<(int ProjectId, string Service), JobRunBrief> _last = new();

    /// <summary>Every job, one entry per service.</summary>
    public IReadOnlyList<Job> All => _all;

    /// <summary>The job a service of a compose project reported by <paramref name="agentId"/> belongs to, or null.</summary>
    public Job? Find(int agentId, string project, string service) =>
        project.Length > 0 && service.Length > 0 ? _byContainer.GetValueOrDefault((agentId, project, service)) : null;

    /// <summary>The job this container runs for — only when it was deployed as one (docker does not restart it by itself).</summary>
    public Job? Find(int agentId, ContainerInfo c) => DeployedAsJob(c) ? Find(agentId, c.Project, c.Service) : null;

    public bool IsJob(int agentId, ContainerInfo c) => Find(agentId, c) != null;

    /// <summary>
    /// A container created as a job: docker does not start it again by itself. An agent too old to report the
    /// policy ("") counts as one.
    /// </summary>
    public static bool DeployedAsJob(ContainerInfo c) => c.RestartPolicy is not ("always" or "unless-stopped");

    /// <summary>The schedule, next time and last run of a job container, for the pages; null when it is no job.</summary>
    public JobInfoDto? Info(int agentId, ContainerInfo c)
    {
        if (Find(agentId, c) is not { } job) return null;
        return new JobInfoDto(Schedules.Describe(job.Spec.Cron), job.Spec.Cron, job.Spec.TimeZone, Schedules.Next(job.Spec, DateTime.UtcNow),
            _last.GetValueOrDefault((job.ProjectId, job.Service)));
    }

    /// <summary>The last run of a job changed (it started or ended).</summary>
    public void SetLast(int projectId, string service, JobRunBrief run) => _last[(projectId, service)] = run;

    /// <summary>Reads the projects again, and the last run of each job it has not seen yet.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var projects = await db.Projects.AsNoTracking().Where(p => p.ComposeYaml.Contains("schedule")).ToListAsync(ct);
        var byName = await ProjectService.AgentIdsByNameAsync(db, ct);
        var all = new List<Job>();
        var map = new Dictionary<(int, string, string), Job>();
        foreach (var p in projects)
        {
            var services = ComposeServices.Parse(p.ComposeYaml);
            var jobs = services.Where(s => s.Schedule != null).ToList();
            if (jobs.Count == 0) continue;
            var agents = ProjectService.AgentIdsOf(p, services, byName).Concat(p.DeployedIds).Concat(p.Extras.Values.SelectMany(x => x.Keys)).ToHashSet();
            foreach (var s in jobs)
            {
                var job = new Job(p.Id, p.Name, s.Name, s.Schedule!, agents);
                all.Add(job);
                foreach (var a in agents) map[(a, p.Name, s.Name)] = job;
            }
        }
        _all = all;
        _byContainer = map;

        var missing = all.Where(j => !_last.ContainsKey((j.ProjectId, j.Service))).ToList();
        foreach (var j in missing)
        {
            var r = await db.JobRuns.AsNoTracking().Where(x => x.ProjectId == j.ProjectId && x.Service == j.Service).OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
            if (r != null) _last[(j.ProjectId, j.Service)] = Brief(r);
        }
    }

    public static JobRunBrief Brief(JobRunEntity r) =>
        new(DateTime.SpecifyKind(r.StartedAt, DateTimeKind.Utc), r.FinishedAt is { } f ? DateTime.SpecifyKind(f, DateTimeKind.Utc) : null, r.Ok, r.ExitCode, r.Note);
}
