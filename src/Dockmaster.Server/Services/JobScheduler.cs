using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Protocol;
using Dockmaster.Server.Data;

namespace Dockmaster.Server.Services;

/// <summary>One run of a job, for the pages.</summary>
public sealed record JobRunInfo(int Id, DateTime StartedAt, DateTime? FinishedAt, int? ExitCode, bool? Ok, string Trigger, string Note, string LogTail);

/// <summary>Where a job stands, for the pages.</summary>
/// <param name="State">running / waiting (for its next time) / undeployed (no container on the server yet) / never (the expression names no time)</param>
/// <param name="NextAt">the next time the schedule names, UTC</param>
/// <param name="ZoneProblem">the time zone is not known to this server: UTC is used</param>
public sealed record JobStatus(string Service, string Cron, string Description, string? TimeZone, string? ZoneProblem, string? Timeout, DateTime? NextAt,
    string State, DateTime? RunningSince, IReadOnlyList<JobRunInfo> Runs);

/// <summary>
/// Jobs: services with <c>x-dockmaster: {schedule: …}</c>. Once a minute the ones whose cron expression named a time
/// since the last minute are started (the agent runs <c>compose start</c> on the container the deploy created) — not
/// while the previous run is still going. What happens to the container afterwards is followed from the heartbeats:
/// each run goes into the database with how it ended, a failed run is an event with the container's last log lines,
/// and a run still going after the job's timeout is stopped. Runs while Dockmaster was not running are missed and said
/// so, not caught up.
/// </summary>
public sealed class JobScheduler : BackgroundService
{
    public const string LastTickKey = "jobs.last_tick";
    public const int RunsKept = 50;
    public const int RunsShown = 20;
    public const string TriggerSchedule = "schedule";
    public const string TriggerManual = "manual";
    public const string TriggerOther = "other";
    /// <summary>A start asked for is matched to the container that starts within this time.</summary>
    private static readonly TimeSpan ExpectFor = TimeSpan.FromMinutes(3);
    /// <summary>A time the schedule named longer ago than this is missed, not started late.</summary>
    private static readonly TimeSpan MissedAfter = TimeSpan.FromMinutes(2);

    /// <summary>What was last seen of a job container: to notice a run starting and ending.</summary>
    private sealed class Seen
    {
        public required Job Job;
        public required int AgentId;
        public required string Name;
        public long StartedUnix;
        public string State = "";
        public int RestartCount;
        public int? RunId;
        public int Retries;
        public bool TimedOut;
    }

    private readonly IDbContextFactory<DockmasterDb> _dbf;
    private readonly AgentRegistry _agents;
    private readonly ProjectService _projects;
    private readonly JobIndex _jobs;
    private readonly Notifier _notifier;
    private readonly SettingsService _settings;
    private readonly ILogger<JobScheduler> _log;
    private readonly ConcurrentDictionary<string, Seen> _seen = new();
    private readonly ConcurrentDictionary<(int ProjectId, string Service), (string Trigger, DateTime At)> _expected = new();
    private readonly HashSet<(int, string)> _failing = [];
    private readonly ConcurrentDictionary<int, byte> _tracking = new();
    private readonly SemaphoreSlim _busy = new(1, 1);
    private DateTime _lastTick;

    public JobScheduler(IDbContextFactory<DockmasterDb> dbf, AgentRegistry agents, ProjectService projects, JobIndex jobs, Notifier notifier, SettingsService settings,
        ILogger<JobScheduler> log)
    {
        _dbf = dbf;
        _agents = agents;
        _projects = projects;
        _jobs = jobs;
        _notifier = notifier;
        _settings = settings;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _jobs.RefreshAsync(stoppingToken);
            _lastTick = DateTime.TryParse(await _settings.GetAsync(LastTickKey, stoppingToken), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                ? t : DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "jobs: first read"); _lastTick = DateTime.UtcNow; }
        _agents.Changed += OnChanged;
        stoppingToken.Register(() => _agents.Changed -= OnChanged);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // just after each full minute: a "0 3 * * *" job starts at 03:00:0x
                var now = DateTime.UtcNow;
                var next = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc).AddMinutes(1).AddSeconds(2);
                await Task.Delay(next - now, stoppingToken);
                try { await TickAsync(DateTime.UtcNow, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { _log.LogWarning(ex, "jobs: minute"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Starts the jobs whose schedule named a time since the last minute, then stops the runs over their timeout.
    /// </summary>
    private async Task TickAsync(DateTime now, CancellationToken ct)
    {
        if (!await _busy.WaitAsync(0, ct)) return; // the last minute is still being worked on
        try
        {
            await _jobs.RefreshAsync(ct);
            var from = _lastTick;
            _lastTick = now;
            await _settings.SetAsync(LastTickKey, now.ToString("O"), ct);
            foreach (var job in _jobs.All)
            {
                var due = Schedules.Between(job.Spec, from, now);
                if (due.Count == 0) continue;
                // the server was not running: what it missed is said, not caught up (a backup at 03:00 is not wanted at 11:00)
                var missed = due.Where(d => now - d > MissedAfter).ToList();
                if (missed.Count > 0)
                    Raise(new DmEvent(DmEvent.Warning, "job", $"job {Name(job)}: missed {(missed.Count == 1 ? "its run" : $"{missed.Count} runs")} while Dockmaster was not running, the last one at {Local(missed[^1], job.Spec)}",
                        ProjectId: job.ProjectId, Service: job.Service));
                if (now - due[^1] <= MissedAfter) await StartAsync(job, due[^1], TriggerSchedule, ct);
            }
            await TimeoutsAsync(now, ct);
            await SweepOpenRunsAsync(now);
        }
        finally { _busy.Release(); }
    }

    /// <summary>Starts a job now, as a person asked from the page.</summary>
    public async Task<(bool Ok, string Message)> RunNowAsync(int projectId, string service, CancellationToken ct)
    {
        await _jobs.RefreshAsync(ct);
        var job = _jobs.All.FirstOrDefault(j => j.ProjectId == projectId && j.Service == service);
        if (job == null) return (false, $"{service} is not a job on a schedule");
        return await StartAsync(job, DateTime.UtcNow, TriggerManual, ct);
    }

    private async Task<(bool Ok, string Message)> StartAsync(Job job, DateTime at, string trigger, CancellationToken ct)
    {
        var key = (job.ProjectId, job.Service);
        var containers = Containers(job);
        // the service as it ran before its schedule was saved: docker restarts it by itself, a start would do nothing
        if (containers.Count > 0 && containers.All(x => !JobIndex.DeployedAsJob(x.Container)))
            return Refused(job, trigger, "it still runs as a service (restart: always) — deploy it to make it a job");
        if (containers.Count == 0 && trigger != TriggerSchedule) return (false, "no container on the server yet — deploy the project first");
        var running = containers.Where(x => x.Container.State == "running").Select(x => x.Container).FirstOrDefault();
        if (running != null)
        {
            var since = FromUnix(running.StartedUnix);
            var message = trigger == TriggerSchedule
                ? $"the {Local(at, job.Spec)} run is skipped — the previous one is still running (since {Local(since, job.Spec)})"
                : $"it is running already (since {Local(since, job.Spec)})";
            if (trigger == TriggerSchedule) Raise(new DmEvent(DmEvent.Warning, "job", $"job {Name(job)}: {message}", ProjectId: job.ProjectId, Service: job.Service));
            return (false, message);
        }
        _expected[key] = (trigger, DateTime.UtcNow);
        (bool Ok, string Message) r;
        try { r = await _projects.RunJobAsync(job.ProjectId, job.Service, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { r = (false, ex.Message); }
        bool first;
        lock (_failing) first = r.Ok ? _failing.Remove(key) && false : _failing.Add(key);
        if (!r.Ok)
        {
            _expected.TryRemove(key, out _);
            // said once until a start works again: every minute would be noise for a job every minute
            if (first) Raise(new DmEvent(DmEvent.Error, "job", $"job {Name(job)}: could not start — {r.Message}", ProjectId: job.ProjectId, Service: job.Service));
        }
        return r;
    }

    /// <summary>A start that cannot be done: said once (until a start works again) when the schedule asked, answered when a person did.</summary>
    private (bool Ok, string Message) Refused(Job job, string trigger, string message)
    {
        bool first;
        lock (_failing) first = _failing.Add((job.ProjectId, job.Service));
        if (trigger == TriggerSchedule && first)
            Raise(new DmEvent(DmEvent.Warning, "job", $"job {Name(job)}: not started — {message}", ProjectId: job.ProjectId, Service: job.Service));
        return (false, message);
    }

    /// <summary>Stops the runs that go on beyond the job's timeout.</summary>
    private async Task TimeoutsAsync(DateTime now, CancellationToken ct)
    {
        foreach (var (id, seen) in _seen)
        {
            if (seen.RunId == null || seen.TimedOut || seen.State != "running" || seen.Job.Spec.TimeoutSpan is not { } timeout) continue;
            var job = _jobs.All.FirstOrDefault(j => j.ProjectId == seen.Job.ProjectId && j.Service == seen.Job.Service) ?? seen.Job;
            if (job.Spec.TimeoutSpan is not { } current || now - FromUnix(seen.StartedUnix) < current) continue;
            seen.TimedOut = true;
            var r = await _agents.SendAsync(seen.AgentId, new Command { ContainerAction = new ContainerAction { ContainerId = id, Op = ContainerOp.ContainerStop } }, TimeSpan.FromSeconds(90), ct);
            Raise(new DmEvent(DmEvent.Warning, "job", $"job {Name(job)}: still running after {Schedules.Duration(current)} — {(r.Ok ? "stopped" : "could not be stopped: " + AgentRegistry.ErrorText(r))}",
                ProjectId: job.ProjectId, AgentId: seen.AgentId, Service: job.Service, Container: seen.Name));
        }
    }

    // ---------- runs, from the heartbeats ----------

    private void OnChanged(int agentId, bool heartbeat)
    {
        if (!heartbeat) return;
        if (_agents.Get(agentId) is not { Last: not null } s) return;
        if (!_tracking.TryAdd(agentId, 0)) return; // the previous heartbeat of this server is still being read
        _ = Task.Run(async () =>
        {
            try { await TrackAsync(s); }
            catch (Exception ex) { _log.LogWarning(ex, "jobs: heartbeat of {agent}", s.Name); }
            finally { _tracking.TryRemove(agentId, out _); }
        });
    }

    /// <summary>A run starts when a job container is running with a new start time, and ends when it is not running any more.</summary>
    private async Task TrackAsync(AgentSession s)
    {
        var now = DateTime.UtcNow;
        var ids = new HashSet<string>();
        foreach (var c in s.Last!.Containers)
        {
            if (_jobs.Find(s.AgentId, c) is not { } job) continue;
            ids.Add(c.Id);
            _seen.TryGetValue(c.Id, out var prev);
            if (c.State == "running")
            {
                // docker's own retries (on-failure) start the same container again: one run
                var retry = prev is { RunId: not null } && prev.StartedUnix != c.StartedUnix && c.RestartCount > prev.RestartCount;
                if (prev is { RunId: not null } && (prev.StartedUnix == c.StartedUnix || retry))
                {
                    if (retry) prev.Retries += c.RestartCount - prev.RestartCount;
                    prev.StartedUnix = c.StartedUnix;
                    prev.State = c.State;
                    prev.RestartCount = c.RestartCount;
                    continue;
                }
                var trigger = _expected.TryRemove((job.ProjectId, job.Service), out var e) && now - e.At < ExpectFor ? e.Trigger : TriggerOther;
                var seen = new Seen { Job = job, AgentId = s.AgentId, Name = c.Name, StartedUnix = c.StartedUnix, State = c.State, RestartCount = c.RestartCount };
                seen.RunId = await OpenRunAsync(job, s.AgentId, c, trigger);
                _seen[c.Id] = seen;
            }
            else if (c.State == "restarting")
            {
                if (prev != null) prev.State = c.State;
            }
            else
            {
                if (prev is { RunId: not null }) await CloseRunAsync(prev, s, c, now);
                else if (prev == null && c.State == "exited" && c.LastExit is { } exit) await PastRunAsync(job, s, c, exit);
                _seen[c.Id] = new Seen { Job = job, AgentId = s.AgentId, Name = c.Name, StartedUnix = c.StartedUnix, State = c.State, RestartCount = c.RestartCount };
            }
        }
        foreach (var gone in _seen.Where(x => x.Value.AgentId == s.AgentId && !ids.Contains(x.Key)).Select(x => x.Key).ToList())
            if (_seen.TryRemove(gone, out var was) && was.RunId is { } runId)
                await EndOpenRunAsync(runId, now, "cut short: the container was removed (a deploy re-created it)");
    }

    /// <summary>A run whose end was not seen: over, neither ok nor failed.</summary>
    private async Task EndOpenRunAsync(int runId, DateTime at, string note)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        if (await db.JobRuns.FindAsync(runId) is not { FinishedAt: null } run) return;
        run.FinishedAt = at;
        run.Note = note;
        await db.SaveChangesAsync();
        _jobs.SetLast(run.ProjectId, run.Service, JobIndex.Brief(run));
    }

    /// <summary>
    /// Runs the database still has open whose container is not running any more on a server that reports: this
    /// server was restarted while they ended, or they belonged to a container that is gone. Closed as cut short.
    /// </summary>
    private async Task SweepOpenRunsAsync(DateTime now)
    {
        List<JobRunEntity> open;
        await using (var db = await _dbf.CreateDbContextAsync())
            open = await db.JobRuns.AsNoTracking().Where(r => r.FinishedAt == null).ToListAsync();
        foreach (var r in open)
        {
            if (_seen.Values.Any(x => x.RunId == r.Id)) continue;
            if (_agents.Get(r.AgentId)?.Last is not { } hb) continue; // a server away: not known
            var c = hb.Containers.FirstOrDefault(x => x.Id == r.ContainerId);
            if (c?.State == "running") continue;
            await EndOpenRunAsync(r.Id, now, c == null ? "cut short: the container was removed" : "its end was not seen (Dockmaster was restarted meanwhile)");
        }
    }

    /// <summary>A run that started: its row (an existing one for the same start, e.g. from before a restart of this server).</summary>
    private async Task<int> OpenRunAsync(Job job, int agentId, ContainerInfo c, string trigger)
    {
        var startedAt = FromUnix(c.StartedUnix);
        await using var db = await _dbf.CreateDbContextAsync();
        var existing = await db.JobRuns.FirstOrDefaultAsync(r => r.ContainerId == c.Id && r.StartedAt == startedAt);
        if (existing != null)
        {
            if (existing.FinishedAt != null) { existing.FinishedAt = null; existing.ExitCode = null; existing.Ok = null; await db.SaveChangesAsync(); }
            return existing.Id;
        }
        var run = new JobRunEntity { ProjectId = job.ProjectId, Service = job.Service, AgentId = agentId, ContainerId = c.Id, StartedAt = startedAt, Trigger = trigger };
        db.JobRuns.Add(run);
        await db.SaveChangesAsync();
        await PruneAsync(db, job);
        _jobs.SetLast(job.ProjectId, job.Service, JobIndex.Brief(run));
        return run.Id;
    }

    /// <summary>A run that ended: how, and an event when it failed (or works again after a failure).</summary>
    private async Task CloseRunAsync(Seen seen, AgentSession s, ContainerInfo c, DateTime now)
    {
        var runId = seen.RunId!.Value;
        seen.RunId = null;
        var startedAt = FromUnix(seen.StartedUnix);
        var exit = c.LastExit is { } e && e.UnixMs >= seen.StartedUnix * 1000 - 1000 ? e : null;
        var code = exit?.ExitCode ?? c.ExitCode;
        var oom = exit?.Oom == true || c.OomKilled;
        var finishedAt = exit != null ? DateTimeOffset.FromUnixTimeMilliseconds(exit.UnixMs).UtcDateTime : now;
        var ok = code == 0 && !oom && !seen.TimedOut;
        var job = seen.Job;
        var note = oom ? "out of memory"
            : seen.TimedOut ? $"stopped after the {Schedules.Duration(job.Spec.TimeoutSpan ?? TimeSpan.Zero)} timeout"
            : seen.Retries > 0 ? $"retried {seen.Retries} time{(seen.Retries == 1 ? "" : "s")}"
            : "";
        bool? previousOk;
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            if (await db.JobRuns.FindAsync(runId) is { } run)
            {
                run.FinishedAt = finishedAt;
                run.ExitCode = code;
                run.Ok = ok;
                run.Note = note;
                run.LogTail = ok ? "" : exit?.LogTail ?? "";
                await db.SaveChangesAsync();
                _jobs.SetLast(job.ProjectId, job.Service, JobIndex.Brief(run));
            }
            previousOk = await db.JobRuns.Where(r => r.ProjectId == job.ProjectId && r.Service == job.Service && r.Id < runId && r.FinishedAt != null)
                .OrderByDescending(r => r.Id).Select(r => r.Ok).FirstOrDefaultAsync();
        }
        var took = Took(finishedAt - startedAt);
        if (!ok)
            Raise(new DmEvent(DmEvent.Warning, "job", $"job {Name(job)} on {s.Name}: run failed — {(seen.TimedOut ? note : HealthWatch.ExitReason(code, oom, c.MemoryLimit))} (after {took})",
                ProjectId: job.ProjectId, AgentId: s.AgentId, Service: job.Service, Container: c.Name, Details: exit?.LogTail is { Length: > 0 } tail ? tail : null));
        else if (previousOk == false)
            Raise(new DmEvent(DmEvent.Info, "job", $"job {Name(job)} on {s.Name}: run ok again (took {took})", ProjectId: job.ProjectId, AgentId: s.AgentId, Service: job.Service, Container: c.Name));
    }

    /// <summary>
    /// A job container first seen already finished (this server was restarted while it ran): its run goes into the
    /// history from the agent's record of the exit, when it is not there yet.
    /// </summary>
    private async Task PastRunAsync(Job job, AgentSession s, ContainerInfo c, ContainerExit exit)
    {
        if (c.StartedUnix <= 0 || exit.UnixMs < c.StartedUnix * 1000 - 1000) return;
        var startedAt = FromUnix(c.StartedUnix);
        await using var db = await _dbf.CreateDbContextAsync();
        var run = await db.JobRuns.FirstOrDefaultAsync(r => r.ContainerId == c.Id && r.StartedAt == startedAt);
        if (run == null)
        {
            run = new JobRunEntity { ProjectId = job.ProjectId, Service = job.Service, AgentId = s.AgentId, ContainerId = c.Id, StartedAt = startedAt, Trigger = TriggerOther };
            db.JobRuns.Add(run);
        }
        else if (run.FinishedAt != null) return;
        run.FinishedAt = DateTimeOffset.FromUnixTimeMilliseconds(exit.UnixMs).UtcDateTime;
        run.ExitCode = exit.ExitCode;
        run.Ok = exit.ExitCode == 0 && !exit.Oom;
        run.Note = exit.Oom ? "out of memory" : "";
        run.LogTail = run.Ok == true ? "" : exit.LogTail;
        await db.SaveChangesAsync();
        await PruneAsync(db, job);
        _jobs.SetLast(job.ProjectId, job.Service, JobIndex.Brief(run));
    }

    private static async Task PruneAsync(DockmasterDb db, Job job)
    {
        var old = await db.JobRuns.Where(r => r.ProjectId == job.ProjectId && r.Service == job.Service).OrderByDescending(r => r.Id).Skip(RunsKept).Select(r => r.Id).ToListAsync();
        if (old.Count > 0) await db.JobRuns.Where(r => old.Contains(r.Id)).ExecuteDeleteAsync();
    }

    // ---------- for the pages ----------

    /// <summary>The jobs of a project, as the scheduler sees them now, with their last runs.</summary>
    public async Task<List<JobStatus>> StatusAsync(int projectId, CancellationToken ct)
    {
        var jobs = _jobs.All.Where(j => j.ProjectId == projectId).ToList();
        if (jobs.Count == 0)
        {
            await _jobs.RefreshAsync(ct); // a schedule saved a moment ago
            jobs = _jobs.All.Where(j => j.ProjectId == projectId).ToList();
        }
        var list = new List<JobStatus>();
        if (jobs.Count == 0) return list;
        var now = DateTime.UtcNow;
        await using var db = await _dbf.CreateDbContextAsync(ct);
        foreach (var job in jobs)
        {
            var runs = (await db.JobRuns.AsNoTracking().Where(r => r.ProjectId == job.ProjectId && r.Service == job.Service).OrderByDescending(r => r.Id).Take(RunsShown).ToListAsync(ct))
                .Select(r => new JobRunInfo(r.Id, Utc(r.StartedAt), Utc(r.FinishedAt), r.ExitCode, r.Ok, r.Trigger, r.Note, r.LogTail)).ToList();
            Schedules.Zone(job.Spec.TimeZone, out var zoneProblem);
            var next = Schedules.Next(job.Spec, now);
            var containers = Containers(job).Where(x => JobIndex.DeployedAsJob(x.Container)).ToList();
            var running = containers.Where(x => x.Container.State == "running").Select(x => x.Container).FirstOrDefault();
            var state = running != null ? "running" : containers.Count == 0 ? "undeployed" : next != null ? "waiting" : "never";
            list.Add(new JobStatus(job.Service, job.Spec.Cron, Schedules.Describe(job.Spec.Cron), job.Spec.TimeZone, zoneProblem, job.Spec.Timeout, next, state,
                running != null ? FromUnix(running.StartedUnix) : null, runs));
        }
        return list;
    }

    private List<(int AgentId, ContainerInfo Container)> Containers(Job job) =>
        job.AgentIds.SelectMany(id => (_agents.Get(id)?.Last?.Containers ?? []).Where(c => c.Project == job.Project && c.Service == job.Service).Select(c => (id, c))).ToList();

    private void Raise(DmEvent e) => _ = _notifier.RaiseAsync(e);

    private static string Name(Job job) => $"{job.Project}/{job.Service}";
    private static DateTime FromUnix(long seconds) => DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, seconds)).UtcDateTime;
    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
    private static DateTime? Utc(DateTime? d) => d == null ? null : Utc(d.Value);

    /// <summary>A UTC time as the job's time zone shows it, for the events.</summary>
    private static string Local(DateTime utc, ScheduleSpec spec)
    {
        var zone = Schedules.Zone(spec.TimeZone, out _);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        return local.ToString("HH:mm", CultureInfo.InvariantCulture) + (zone == TimeZoneInfo.Utc ? " UTC" : "");
    }

    /// <summary>"12 s", "3 min", "1.5 h".</summary>
    public static string Took(TimeSpan t) => t.TotalMinutes < 1 ? $"{Math.Max(0, t.TotalSeconds):0} s" : t.TotalHours < 1 ? $"{t.TotalMinutes:0} min" : $"{t.TotalHours:0.#} h";
}
