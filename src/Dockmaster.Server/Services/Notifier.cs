using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Server.Access;
using Dockmaster.Server.Api;
using Dockmaster.Server.Data;
using Dockmaster.Server.Hubs;

namespace Dockmaster.Server.Services;

/// <summary>Something that happened that a person may want to know about, now or later.</summary>
/// <param name="Kind">deploy, server, container, host, service, memory, autoscale, audit (what a person did), test</param>
/// <param name="Data">for whoever raises it (not stored, not sent)</param>
/// <param name="Container">the container it is about, by name</param>
/// <param name="Details">more to it, e.g. the last log lines of a container that stopped</param>
/// <param name="User">the signed-in user who did it (an audit event)</param>
public sealed record DmEvent(string Level, string Kind, string Message, int? ProjectId = null, int? AgentId = null, string? Service = null,
    IReadOnlyDictionary<string, object?>? Data = null, string? Container = null, string? Details = null, string? User = null)
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Error = "error";

    /// <summary>The kind of what a person did: users, roles, projects, secrets, servers, containers.</summary>
    public const string Audit = "audit";
}

/// <summary>
/// Events: kept in the database (the Events page, the dashboard, the project page), pushed to the open pages and,
/// when a Seq server is set in Settings, sent there — Seq's own alerts (e-mail through its SMTP) take it from there.
/// </summary>
public sealed class Notifier : BackgroundService
{
    public const string SeqUrlKey = "seq.url";
    public const string SeqApiKeyKey = "seq.apikey";
    /// <summary>When the Seq address was set: events from before stay out of it.</summary>
    public const string SeqSinceKey = "seq.since";
    private static readonly TimeSpan KeepEvents = TimeSpan.FromDays(30);
    private const int Batch = 100;

    private readonly IDbContextFactory<DockmasterDb> _dbf;
    private readonly IHubContext<LiveHub> _hub;
    private readonly AccessService _access;
    private readonly SettingsService _settings;
    private readonly SecretProtector _secrets;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<Notifier> _log;
    // a new event wakes the sender; the events themselves wait in the table until Seq took them
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public Notifier(IDbContextFactory<DockmasterDb> dbf, IHubContext<LiveHub> hub, SettingsService settings, SecretProtector secrets, IHttpClientFactory http, AccessService access,
        ILogger<Notifier> log)
    {
        _access = access;
        _dbf = dbf;
        _hub = hub;
        _settings = settings;
        _secrets = secrets;
        _http = http;
        _log = log;
    }

    /// <summary>Records the event and passes it on; never throws (a notice must not break what it reports on).</summary>
    public async Task RaiseAsync(DmEvent e)
    {
        try
        {
            string? project = null, agent = null;
            int? projectServer = null;
            EventEntity row;
            await using (var db = await _dbf.CreateDbContextAsync())
            {
                if (e.ProjectId is { } pid && await db.Projects.Where(p => p.Id == pid).Select(p => new { p.Name, p.AgentId }).FirstOrDefaultAsync() is { } of)
                    (project, projectServer) = (of.Name, of.AgentId);
                // what a person did to a project is about its server too: the server's people still see it once the
                // project is deleted, and a container's page finds it by server and name
                if (e.Kind == DmEvent.Audit && e.AgentId == null) e = e with { AgentId = projectServer };
                if (e.AgentId is { } aid) agent = await db.Agents.Where(a => a.Id == aid).Select(a => a.Name).FirstOrDefaultAsync();
                row = new EventEntity { At = DateTime.UtcNow, Level = e.Level, Kind = e.Kind, ProjectId = e.ProjectId, AgentId = e.AgentId, Service = e.Service, Message = e.Message,
                    Container = e.Container, Details = e.Details, User = e.User };
                db.Events.Add(row);
                await db.SaveChangesAsync();
            }
            _log.Log(e.Level switch { DmEvent.Error => LogLevel.Error, DmEvent.Warning => LogLevel.Warning, _ => LogLevel.Information }, "{kind}: {message}",
                e.Kind, e.User != null ? $"{e.User}: {e.Message}" : e.Message);
            // to the users who see its server (or its project's); one about neither goes to the admins
            await _hub.Clients.Users(await _access.WhoSeesAsync(e.AgentId ?? projectServer)).SendAsync("event", ToDto(row, project, agent));
            _wake.Writer.TryWrite(true);
        }
        catch (Exception ex) { _log.LogWarning(ex, "event not recorded: {message}", e.Message); }
    }

    /// <summary>What a signed-in user did, for the Events page and Seq: an info event of kind audit naming them.</summary>
    /// <param name="agentId">the server it is about (a project's own server when left out); neither it nor a project: only the admins get it</param>
    public Task AuditAsync(UserAccess who, string message, int? projectId = null, int? agentId = null, string? container = null) =>
        AuditAsync(who.Name, message, projectId, agentId, container);

    /// <param name="who">a user's name, or what else did it when nobody signed in did ("API key backend")</param>
    public Task AuditAsync(string who, string message, int? projectId = null, int? agentId = null, string? container = null) =>
        RaiseAsync(new DmEvent(DmEvent.Info, DmEvent.Audit, message, projectId, agentId, Container: container, User: who));

    public static EventDto ToDto(EventEntity e, string? project, string? agent) =>
        new(e.Id, DateTime.SpecifyKind(e.At, DateTimeKind.Utc), e.Level, e.Kind, e.ProjectId, project, e.AgentId, agent, e.Service, e.Message, e.Container, e.Details, e.User);

    /// <summary>
    /// Sends what Seq has not got yet, oldest first, a batch at a time; while Seq is away the events wait in the table
    /// (also over a restart) and the tries slow down to one every 5 minutes.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastTrim = DateTime.MinValue;
        var failures = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var pause = failures == 0 ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(Math.Min(300, 5 << Math.Min(failures, 6)));
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                {
                    wait.CancelAfter(pause);
                    // after a failure a new event does not make it try again at once
                    try { if (failures == 0) await _wake.Reader.ReadAsync(wait.Token); else await Task.Delay(pause, wait.Token); }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
                }
                // one bad pass (the database busy, Seq answering nonsense) must not end the loop: an exception leaving a
                // background service stops the whole server
                try
                {
                    if (DateTime.UtcNow - lastTrim > TimeSpan.FromHours(6))
                    {
                        lastTrim = DateTime.UtcNow;
                        await TrimAsync(stoppingToken);
                    }
                    failures = await SendPendingAsync(stoppingToken) ? 0 : failures + 1;
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _log.LogWarning(ex, "events not sent to Seq");
                    failures++;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>False when Seq did not take a batch.</summary>
    private async Task<bool> SendPendingAsync(CancellationToken ct)
    {
        if (await SeqAsync(ct) is not { } seq) return true;
        var since = DateTime.TryParse(await _settings.GetAsync(SeqSinceKey, ct), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : DateTime.MinValue;
        while (true)
        {
            List<EventEntity> rows;
            Dictionary<int, string> projects, agents;
            await using (var db = await _dbf.CreateDbContextAsync(ct))
            {
                rows = await db.Events.AsNoTracking().Where(e => !e.SentToSeq && e.At >= since).OrderBy(e => e.Id).Take(Batch).ToListAsync(ct);
                if (rows.Count == 0) return true;
                projects = await db.ProjectNamesAsync(ct);
                agents = await db.AgentNamesAsync(ct);
            }
            var (ok, error) = await PostAsync(seq, rows.Select(e => (DateTime.SpecifyKind(e.At, DateTimeKind.Utc), new DmEvent(e.Level, e.Kind, e.Message, e.ProjectId, e.AgentId, e.Service, User: e.User),
                e.ProjectId is { } p ? projects.GetValueOrDefault(p) : null, e.AgentId is { } a ? agents.GetValueOrDefault(a) : null)), ct);
            if (!ok)
            {
                _log.LogWarning("events not sent to Seq: {error}", error);
                return false;
            }
            var ids = rows.Select(r => r.Id).ToList();
            await using (var db = await _dbf.CreateDbContextAsync(ct))
                await db.Events.Where(e => ids.Contains(e.Id)).ExecuteUpdateAsync(u => u.SetProperty(e => e.SentToSeq, true), ct);
            if (rows.Count < Batch) return true;
        }
    }

    private sealed record SeqTarget(string Url, string? ApiKey);

    private async Task<SeqTarget?> SeqAsync(CancellationToken ct)
    {
        var url = (await _settings.GetAsync(SeqUrlKey, ct))?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(url)) return null;
        return new SeqTarget(url, await StoredKeyAsync(ct));
    }

    /// <summary>The stored Seq API key; null when there is none or it cannot be decrypted any more.</summary>
    public async Task<string?> StoredKeyAsync(CancellationToken ct)
    {
        var key = await _settings.GetAsync(SeqApiKeyKey, ct);
        if (string.IsNullOrEmpty(key)) return null;
        try { return _secrets.Unprotect(key); }
        catch (InvalidOperationException) { return null; }
    }

    private async Task<(bool Ok, string? Error)> PostAsync(SeqTarget seq, IEnumerable<(DateTime At, DmEvent Event, string? Project, string? Agent)> events, CancellationToken ct)
    {
        var body = new StringBuilder();
        foreach (var (at, e, project, agent) in events)
        {
            var o = new Dictionary<string, object?>
            {
                ["@t"] = at.ToString("O"),
                ["@m"] = e.User != null ? $"{e.User}: {e.Message}" : e.Message,
                ["@l"] = e.Level switch { DmEvent.Error => "Error", DmEvent.Warning => "Warning", _ => "Information" },
                ["Application"] = "Dockmaster",
                ["Kind"] = e.Kind,
            };
            if (project != null) o["Project"] = project;
            if (agent != null) o["Server"] = agent;
            if (e.Service != null) o["Service"] = e.Service;
            if (e.User != null) o["User"] = e.User;
            body.Append(JsonSerializer.Serialize(o)).Append('\n');
        }
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, seq.Url + "/ingest/clef") { Content = new StringContent(body.ToString(), Encoding.UTF8) };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.serilog.clef");
            if (seq.ApiKey != null) req.Headers.Add("X-Seq-ApiKey", seq.ApiKey);
            using var res = await _http.CreateClient("seq").SendAsync(req, ct);
            return res.IsSuccessStatusCode ? (true, null) : (false, $"Seq answered {(int)res.StatusCode} {res.ReasonPhrase}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return (false, ex.Message); }
    }

    /// <summary>Sends one event to this Seq right away (the Settings page's test), with Seq's answer.</summary>
    public async Task<(bool Ok, string? Error)> TestAsync(string url, string? apiKey, CancellationToken ct)
    {
        var e = new DmEvent(DmEvent.Info, "test", "Dockmaster test event: Seq receives what Dockmaster reports");
        return await PostAsync(new SeqTarget(url.Trim().TrimEnd('/'), string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim()), [(DateTime.UtcNow, e, null, null)], ct);
    }

    private async Task TrimAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbf.CreateDbContextAsync(ct);
            var before = DateTime.UtcNow - KeepEvents;
            await db.Events.Where(e => e.At < before).ExecuteDeleteAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogDebug(ex, "trimming events"); }
    }
}
