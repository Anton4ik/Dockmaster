using System.Collections.Concurrent;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Dockmaster.Agent.Docker;
using Dockmaster.Protocol;
using Outcome = Dockmaster.Agent.CommandDispatcher.Outcome;

namespace Dockmaster.Agent;

/// <summary>
/// Keeps one outbound gRPC stream to the server open (reconnecting with backoff),
/// sends heartbeats with host + container stats and executes commands the server sends.
/// </summary>
public sealed class HubClient : BackgroundService
{
    // reconnect backoff: doubles from the first delay up to the last; a session that held this long resets it
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LongestRetry = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WorkingSession = TimeSpan.FromSeconds(30);

    private readonly AgentOptions _opts;
    private readonly DockerService _docker;
    private readonly HostStatsCollector _host;
    private readonly CommandDispatcher _dispatcher;
    private readonly MeshManager _mesh;
    private readonly ExitWatcher _exits;
    private readonly ILogger<HubClient> _log;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private CancellationToken _stopping;
    // how often to report, as the server last said; a faster pace ends the current wait
    private readonly Pacing _heartbeat = new();

    public HubClient(IOptions<AgentOptions> opts, DockerService docker, HostStatsCollector host, CommandDispatcher dispatcher, MeshManager mesh, ExitWatcher exits, ILogger<HubClient> log)
    {
        _opts = opts.Value;
        _docker = docker;
        _host = host;
        _dispatcher = dispatcher;
        _mesh = mesh;
        _exits = exits;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        var delay = FirstRetry;
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            try
            {
                await SessionAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // a session that held for a while was a working one (server restart, not a bad address): retry quickly
                if (DateTime.UtcNow - started > WorkingSession) delay = FirstRetry;
                _log.LogWarning("connection to {Url} lost: {Message} — retry in {Delay}s", _opts.ServerUrl, ex.Message, delay.TotalSeconds);
            }
            try { await Task.Delay(delay, stoppingToken); } catch (OperationCanceledException) { break; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, LongestRetry.TotalSeconds));
        }
    }

    private async Task SessionAsync(CancellationToken ct)
    {
        using var channel = ServerChannel.Create(_opts, _log);
        var client = new AgentHub.AgentHubClient(channel);
        using var call = client.Connect(cancellationToken: ct);
        var sendLock = new SemaphoreSlim(1, 1);
        async Task Send(AgentMessage m)
        {
            await sendLock.WaitAsync(ct);
            try { await call.RequestStream.WriteAsync(m, ct); }
            finally { sendLock.Release(); }
        }

        var version = await _docker.VersionAsync(ct);
        var info = await _docker.InfoAsync(ct);
        await Send(new AgentMessage
        {
            Hello = new Hello
            {
                Token = _opts.Token,
                AgentVersion = BuildInfo.Version(typeof(HubClient).Assembly),
                Host = new HostInfo
                {
                    Hostname = _host.ReadHostname(),
                    Os = string.Join(' ', (info.OperatingSystem ?? version.Os ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), // Synology reports a multi-line OS name
                    Arch = version.Arch,
                    DockerVersion = version.Version,
                    Cpus = (int)info.NCPU,
                    MemoryTotal = info.MemTotal,
                    AgentIp = HostStatsCollector.GuessLocalIp(),
                    NoCpuLimits = !info.CPUCfsQuota,
                },
                DataAcks = true,
                SecretVariables = true,
            }
        });

        // the welcome first; a pace may come ahead of it from a server that registered the session before its welcome
        Pace? early = null;
        ServerMessage first;
        while (true)
        {
            if (!await call.ResponseStream.MoveNext(ct)) throw new RpcException(new Status(StatusCode.Unavailable, "server closed stream"));
            first = call.ResponseStream.Current;
            if (first.BodyCase != ServerMessage.BodyOneofCase.Pace) break;
            early = first.Pace;
        }
        if (first.BodyCase != ServerMessage.BodyOneofCase.Welcome) throw new RpcException(new Status(StatusCode.Unauthenticated, "no welcome"));
        var welcome = first.Welcome;
        _log.LogInformation("connected to {Url} as agent '{Name}'", _opts.ServerUrl, welcome.AgentName);
        if (await _docker.DataDirProblemAsync(_opts.DataDir, ct) is { } problem) _log.LogWarning("{Problem}", problem);
        _heartbeat.Set(TimeSpan.FromSeconds(welcome.HeartbeatSeconds > 0 ? welcome.HeartbeatSeconds : _opts.HeartbeatSeconds));
        if (early != null) ApplyPace(early);

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = Task.Run(async () =>
        {
            while (!sessionCts.IsCancellationRequested)
            {
                try
                {
                    var containers = await _docker.ListContainersAsync(sessionCts.Token);
                    var ids = new HashSet<string>(containers.Length);
                    foreach (var c in containers)
                    {
                        c.LastExit = _exits.Last(c.Id);
                        ids.Add(c.Id);
                    }
                    _exits.Keep(ids);
                    var msg = new AgentMessage { Heartbeat = new Heartbeat { Host = _host.Collect(info.DockerRootDir), UnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Mesh = _mesh.Cached } };
                    msg.Heartbeat.Containers.AddRange(containers);
                    await Send(msg);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogWarning("heartbeat failed: {Message}", ex.Message); }
                try { await _heartbeat.WaitAsync(sessionCts.Token); } catch (OperationCanceledException) { break; }
            }
        }, sessionCts.Token);

        // read now: commands that outlive the session still hold it after sessionCts is disposed
        var sessionToken = sessionCts.Token;
        try
        {
            while (await call.ResponseStream.MoveNext(ct))
            {
                var msg = call.ResponseStream.Current;
                // no token for Task.Run: a deploy must start even when the session ends in between; the command is
                // known before it runs, so a cancel right behind it finds it
                if (msg.BodyCase == ServerMessage.BodyOneofCase.Command)
                {
                    var cmd = msg.Command;
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(CommandDispatcher.EndsWithSession(cmd) ? sessionToken : _stopping);
                    _running[cmd.Id] = cts;
                    _ = Task.Run(() => HandleCommandAsync(cmd, Send, sessionToken, cts));
                }
                else
                {
                    // one message that cannot be handled must not end the session (and every command on it)
                    try
                    {
                        if (msg.BodyCase == ServerMessage.BodyOneofCase.Cancel)
                            CancelCommand(msg.Cancel.CommandId);
                        else if (msg.BodyCase == ServerMessage.BodyOneofCase.ExecInput)
                            await _dispatcher.ExecInputAsync(msg.ExecInput);
                        else if (msg.BodyCase == ServerMessage.BodyOneofCase.Data)
                            await _dispatcher.DataAsync(msg.Data);
                        else if (msg.BodyCase == ServerMessage.BodyOneofCase.DataAck)
                            _dispatcher.DataAck(msg.DataAck);
                        else if (msg.BodyCase == ServerMessage.BodyOneofCase.Pace)
                            ApplyPace(msg.Pace);
                    }
                    catch (Exception ex) { _log.LogWarning("{Kind} from the server not handled: {Error}", msg.BodyCase, ex.Message); }
                }
            }
        }
        finally
        {
            // ends log streams and listings; commands that change things run on (see HandleCommandAsync)
            sessionCts.Cancel();
            try { await heartbeat; } catch { }
        }
        throw new RpcException(new Status(StatusCode.Unavailable, "server closed stream"));
    }

    /// <param name="cts">
    /// A lost connection must not cancel a deploy or a re-create: when the container being replaced is the Dockmaster
    /// server itself, stopping it is what drops the connection, and cancelling there left the server stopped. Such
    /// commands end only when the server cancels them or the agent shuts down; the others end with the session.
    /// </param>
    private async Task HandleCommandAsync(Command cmd, Func<AgentMessage, Task> send, CancellationToken sessionCt, CancellationTokenSource cts)
    {
        var bound = CommandDispatcher.EndsWithSession(cmd);
        using var owned = cts;
        Outcome outcome;
        try
        {
            outcome = await _dispatcher.DispatchAsync(cmd, send, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (bound && sessionCt.IsCancellationRequested) return; // connection gone: nobody to answer
            outcome = new Outcome(false, "cancelled");
        }
        catch (Exception ex)
        {
            // a Docker request that ran out of time (TimeoutException), or a cancel that was not ours
            _log.LogError(ex, "command {Command} ({Kind}) failed", cmd.Id, cmd.BodyCase);
            outcome = new Outcome(false, ex is OperationCanceledException or TimeoutException ? "timed out talking to Docker" : ex.Message);
        }
        finally { _running.TryRemove(cmd.Id, out _); }
        try { await send(new AgentMessage { Result = new CommandResult { CommandId = cmd.Id, Ok = outcome.Ok, Message = outcome.Message, Payload = outcome.Payload } }); }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "result of {Command} not delivered", cmd.Id);
            if (!bound) _log.LogInformation("{Kind} {Command} finished after the connection was lost: {Ok} {Message}", cmd.BodyCase, cmd.Id, outcome.Ok ? "ok" : "failed", outcome.Message);
        }
    }

    /// <summary>A server nobody looks at reports every half minute, one on screen every few seconds.</summary>
    private void ApplyPace(Pace pace)
    {
        if (pace.HeartbeatSeconds > 0) _heartbeat.Set(TimeSpan.FromSeconds(pace.HeartbeatSeconds));
        if (pace.MeshSeconds > 0) _mesh.Every(TimeSpan.FromSeconds(pace.MeshSeconds));
    }

    private void CancelCommand(string id)
    {
        if (_running.TryGetValue(id, out var c)) c.Cancel();
    }
}
