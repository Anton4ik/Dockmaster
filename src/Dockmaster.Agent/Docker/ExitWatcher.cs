using System.Collections.Concurrent;
using System.Text;
using Dockmaster.Protocol;

namespace Dockmaster.Agent.Docker;

/// <summary>
/// Docker's container events, followed as they come. How each container's last run ended, with its last log lines,
/// from the die and oom events (docker itself keeps the exit code only until the container runs again, so a restart
/// would hide why it happened); and every event marks its container for the next heartbeat's inspect, so a
/// container nothing happened to is not inspected on every heartbeat.
/// </summary>
public sealed class ExitWatcher(DockerService docker, ILogger<ExitWatcher> log) : BackgroundService
{
    private const int TailLines = 40;
    private const int TailChars = 4000;
    // an oom event this close before a die is why it died
    private const long OomWindowMs = 10_000;
    // the log lines go out with the heartbeats this long: the server has them by then
    private static readonly TimeSpan TailFor = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, ContainerExit> _exits = new();
    private readonly ConcurrentDictionary<string, long> _oom = new();

    /// <summary>The container's last exit for a heartbeat, the log lines only while it is recent; null when none was seen.</summary>
    public ContainerExit? Last(string id)
    {
        if (!_exits.TryGetValue(id, out var e)) return null;
        if (e.LogTail.Length == 0 || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - e.UnixMs < TailFor.TotalMilliseconds) return e;
        return new ContainerExit { UnixMs = e.UnixMs, ExitCode = e.ExitCode, Oom = e.Oom };
    }

    /// <summary>Forgets the containers that are gone.</summary>
    public void Keep(IReadOnlySet<string> ids)
    {
        foreach (var id in _exits.Keys.Where(id => !ids.Contains(id))) _exits.TryRemove(id, out _);
        foreach (var id in _oom.Keys.Where(id => !ids.Contains(id))) _oom.TryRemove(id, out _);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // every event that changes what an inspect says: state, exit code, restart count, health, limits, name
        var filters = new Dictionary<string, string[]>
        {
            ["type"] = ["container"],
            ["event"] = ["die", "oom", "start", "restart", "kill", "stop", "pause", "unpause", "health_status", "update", "rename", "create"],
        };
        while (!stoppingToken.IsCancellationRequested)
        {
            // what happened while the stream was away is unknown: everything is inspected once
            docker.AllChanged();
            try
            {
                await docker.Api.MonitorEventsAsync(filters, OnEvent, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogDebug("docker events: {Error}", ex.Message); }
            // the stream ended (docker restarted): follow it again; until then every heartbeat inspects everything
            docker.AllChanged();
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    private void OnEvent(Message m)
    {
        var id = m.Actor?.ID ?? m.ID;
        if (string.IsNullOrEmpty(id)) return;
        docker.Changed(id);
        var at = m.TimeNano > 0 ? m.TimeNano / 1_000_000 : m.Time > 0 ? m.Time * 1000 : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (m.Action == "oom") { _oom[id] = at; return; }
        if (m.Action != "die") return;
        var code = m.Actor?.Attributes?.TryGetValue("exitCode", out var c) == true && int.TryParse(c, out var n) ? n : -1;
        var oom = _oom.TryRemove(id, out var oomAt) && at - oomAt <= OomWindowMs;
        var exit = new ContainerExit { UnixMs = at, ExitCode = code, Oom = oom };
        _exits[id] = exit;
        _ = Task.Run(async () =>
        {
            // a new message, not this one changed: a heartbeat may be sending it right now
            try { _exits.TryUpdate(id, new ContainerExit(exit) { LogTail = await TailAsync(id, at) }, exit); }
            catch (Exception ex) { log.LogDebug("last logs of {Id}: {Error}", id, ex.Message); }
        });
    }

    /// <summary>The run's last lines, up to the moment it ended (a restarted container is writing new ones already).</summary>
    private async Task<string> TailAsync(string id, long unixMs)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var insp = await docker.InspectAsync(id, cts.Token);
        using var stream = await docker.Api.LogsAsync(id, insp.Config.Tty, $"stdout=1&stderr=1&tail={TailLines}&until={unixMs / 1000 + 1}", cts.Token);
        var text = new StringBuilder();
        await stream.ReadTextAsync(piece => { text.Append(piece); return Task.CompletedTask; }, cts.Token);
        var s = text.ToString().TrimEnd();
        return s.Length > TailChars ? s[^TailChars..] : s;
    }
}
