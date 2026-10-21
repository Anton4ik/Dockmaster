using System.Net;
using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Protocol;
using Dockmaster.Server.Data;

namespace Dockmaster.Server.Services;

/// <summary>gRPC endpoint agents connect to. One long-lived bidirectional stream per agent.</summary>
public sealed class AgentHubService : AgentHub.AgentHubBase
{
    /// <summary>How often a connected agent's LastSeenAt is written (its heartbeats come far more often).</summary>
    private static readonly TimeSpan LastSeenInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan UnknownTokenDelay = TimeSpan.FromMilliseconds(1500);
    /// <summary>How long a new connection may take to say hello (with its token) before it is dropped.</summary>
    public static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);
    /// <summary>
    /// The largest message an agent may send: a heartbeat with every container, a command's result (inspect, image or
    /// volume lists, an imported compose file) or a 64 KB volume chunk. Read before the token is checked.
    /// </summary>
    public const int MaxMessageSize = 16 * 1024 * 1024;
    /// <summary>
    /// How long one message may take to go out. An agent that stops reading (a stalled connection) would otherwise hold
    /// the stream for every sender after it; past this its session ends and it reconnects.
    /// </summary>
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(60);

    private readonly AgentRegistry _registry;
    private readonly Pacer _pacer;
    private readonly IDbContextFactory<DockmasterDb> _dbFactory;
    private readonly ILogger<AgentHubService> _log;
    private readonly CancellationToken _stopping;

    public AgentHubService(AgentRegistry registry, Pacer pacer, IDbContextFactory<DockmasterDb> dbFactory, ILogger<AgentHubService> log, IHostApplicationLifetime lifetime)
    {
        _registry = registry;
        _pacer = pacer;
        _dbFactory = dbFactory;
        _log = log;
        _stopping = lifetime.ApplicationStopping;
    }

    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));

    public override async Task Connect(IAsyncStreamReader<AgentMessage> requestStream, IServerStreamWriter<ServerMessage> responseStream, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        using (var helloWait = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            helloWait.CancelAfter(HelloTimeout);
            try
            {
                if (!await requestStream.MoveNext(helloWait.Token)) return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new RpcException(new Status(StatusCode.DeadlineExceeded, "no hello in time"));
            }
        }
        var first = requestStream.Current;
        if (first.BodyCase != AgentMessage.BodyOneofCase.Hello)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "expected hello"));
        var hello = first.Hello;
        if (hello.Host == null) throw new RpcException(new Status(StatusCode.InvalidArgument, "hello without host info"));

        AgentEntity? agent;
        var peerIp = PeerIp(context.Peer);
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var hash = HashToken(hello.Token);
            agent = await db.Agents.FirstOrDefaultAsync(a => a.TokenHash == hash, ct);
            if (agent == null) _log.LogWarning("agent from {Peer} presented an unknown token", context.Peer);
            else
            {
                agent.LastSeenAt = DateTime.UtcNow;
                agent.Hostname = hello.Host.Hostname;
                agent.Os = hello.Host.Os;
                agent.Arch = hello.Host.Arch;
                agent.DockerVersion = hello.Host.DockerVersion;
                agent.Cpus = hello.Host.Cpus;
                agent.MemoryTotal = hello.Host.MemoryTotal;
                // inside a container the agent only sees the docker bridge address; the peer address is more useful then
                agent.AgentIp = IsDockerBridge(hello.Host.AgentIp) || string.IsNullOrEmpty(hello.Host.AgentIp) ? peerIp : hello.Host.AgentIp;
                agent.PeerIp = peerIp;
                agent.AgentVersion = hello.AgentVersion;
                await db.SaveChangesAsync(ct);
            }
        }
        if (agent == null)
        {
            await Task.Delay(UnknownTokenDelay, ct); // slow down brute force, outside the database context
            throw new RpcException(new Status(StatusCode.Unauthenticated, "unknown token"));
        }

        var sendLock = new SemaphoreSlim(1, 1);
        // Ends with the server too: Kestrel waits for open requests on shutdown and an agent stream never ends by
        // itself, so the server hung until docker killed it (exit 137, which Synology reports as a crash).
        // Not disposed: a reconnect or a delete may still cancel the old session after this stream ended. The hook
        // on the app's stopping token is disposed instead, or every reconnect would leave one behind.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var onStopping = _stopping.Register(cts.Cancel);
        var session = new AgentSession
        {
            AgentId = agent.Id,
            Name = agent.Name,
            Host = hello.Host,
            PeerIp = peerIp,
            DataAcks = hello.DataAcks,
            SecretVariables = hello.SecretVariables,
            Cts = cts,
            Writer = async (m, callerCt) =>
            {
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, callerCt)) await sendLock.WaitAsync(wait.Token);
                try
                {
                    try { await responseStream.WriteAsync(m, cts.Token).WaitAsync(WriteTimeout); }
                    catch (TimeoutException)
                    {
                        _log.LogWarning("agent '{name}' took no message for {s}s: dropping the connection", agent.Name, WriteTimeout.TotalSeconds);
                        cts.Cancel();
                        throw new IOException($"the agent took no message for {WriteTimeout.TotalSeconds:0}s");
                    }
                }
                finally { sendLock.Release(); }
            }
        };
        // The welcome goes out before the session is registered: an agent takes the first message for its welcome and
        // drops the connection on anything else, and once registered the session is anybody's to send to (a pace, a
        // mesh config). It carries the pace of the moment; an agent from before Pace keeps that for the session.
        var pace = _pacer.For(agent.Id);
        _pacer.Told(session, pace);
        await session.Send(new ServerMessage { Welcome = new Welcome { AgentId = agent.Id.ToString(), AgentName = agent.Name, HeartbeatSeconds = pace.HeartbeatSeconds } });
        _registry.Register(session);
        _log.LogInformation("agent '{name}' connected from {peer} ({host}, docker {dv})", agent.Name, context.Peer, hello.Host.Hostname, hello.Host.DockerVersion);

        try
        {
            var lastDbTouch = DateTime.UtcNow;
            while (await requestStream.MoveNext(cts.Token))
            {
                var msg = requestStream.Current;
                switch (msg.BodyCase)
                {
                    case AgentMessage.BodyOneofCase.Heartbeat:
                        _registry.OnHeartbeat(session, msg.Heartbeat);
                        if (DateTime.UtcNow - lastDbTouch > LastSeenInterval)
                        {
                            lastDbTouch = DateTime.UtcNow;
                            await TouchAsync(agent.Id, cts.Token);
                        }
                        break;
                    case AgentMessage.BodyOneofCase.Progress: _registry.OnProgress(session, msg.Progress); break;
                    case AgentMessage.BodyOneofCase.Result: _registry.OnResult(session, msg.Result); break;
                    case AgentMessage.BodyOneofCase.Logs: _registry.OnLog(session, msg.Logs); break;
                    case AgentMessage.BodyOneofCase.Data: _registry.OnData(session, msg.Data); break;
                    case AgentMessage.BodyOneofCase.DataAck: _registry.OnDataAck(session, msg.DataAck); break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Cancelled or StatusCode.Unavailable) { }
        finally
        {
            _registry.Unregister(session);
            cts.Cancel();
            await TouchAsync(agent.Id, CancellationToken.None);
            _log.LogInformation("agent '{name}' disconnected", agent.Name);
        }
    }

    /// <summary>gRPC peer strings look like "ipv4:1.2.3.4:5678" or "ipv6:[::ffff:1.2.3.4]:5678".</summary>
    private static string PeerIp(string peer)
    {
        var s = peer.StartsWith("ipv4:") ? peer[5..] : peer.StartsWith("ipv6:") ? peer[5..] : peer;
        if (s.StartsWith('['))
        {
            var end = s.IndexOf(']');
            if (end > 0) s = s[1..end];
        }
        else
        {
            var colon = s.LastIndexOf(':');
            if (colon > 0 && s.IndexOf(':') == colon) s = s[..colon];
        }
        if (IPAddress.TryParse(s, out var ip) && ip.IsIPv4MappedToIPv6) return ip.MapToIPv4().ToString();
        return s;
    }

    private static bool IsDockerBridge(string? ip) =>
        ip != null && IPAddress.TryParse(ip, out var a) && a.GetAddressBytes() is [172, >= 16 and <= 31, _, _];

    private async Task TouchAsync(int agentId, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            await db.Agents.Where(a => a.Id == agentId).ExecuteUpdateAsync(s => s.SetProperty(a => a.LastSeenAt, DateTime.UtcNow), ct);
        }
        catch (Exception ex) { _log.LogDebug(ex, "touch"); }
    }
}
