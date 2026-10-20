using System.Collections.Concurrent;
using Dockmaster.Protocol;

namespace Dockmaster.Server.Services;

public sealed class PendingCommand
{
    public TaskCompletionSource<CommandResult> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Action<string>? OnProgress { get; init; }
    public Action<LogChunk>? OnLog { get; init; }
    public Action<byte[]>? OnData { get; init; }
}

/// <summary>The chunks that may still go to one running import before the agent acks the ones it has written.</summary>
public sealed class DataWindow
{
    public const int Chunks = 32;
    public SemaphoreSlim Free { get; } = new(Chunks);
    /// <summary>Cancelled when the import is over: nobody waits for its acks any more.</summary>
    public CancellationTokenSource Over { get; } = new();
}

/// <summary>Live state of one connected agent.</summary>
public sealed class AgentSession
{
    public required int AgentId { get; init; }
    public required string Name { get; set; }
    public required HostInfo Host { get; init; }
    /// <summary>Address the connection came from (the host's public address, or its NAT's).</summary>
    public string PeerIp { get; init; } = "";
    /// <summary>Writes one message to the agent; the token only limits the wait for the stream (a write is not cut in half).</summary>
    public required Func<ServerMessage, CancellationToken, Task> Writer { get; init; }
    public required CancellationTokenSource Cts { get; init; }
    public Heartbeat? Last { get; set; }
    public ConcurrentDictionary<string, PendingCommand> Pending { get; } = new();
    /// <summary>The agent acks the DataChunks it is sent and waits for acks of its exports (older agents do neither).</summary>
    public bool DataAcks { get; init; }
    /// <summary>The agent hands a project's secrets to compose as ${VARIABLES} too (older agents only write them as files).</summary>
    public bool SecretVariables { get; init; }
    /// <summary>Running imports by command id, when the agent acks.</summary>
    public ConcurrentDictionary<string, DataWindow> Windows { get; } = new();

    public Task Send(ServerMessage m, CancellationToken ct = default) => Writer(m, ct);
}

/// <summary>All connected agents + command dispatch. Singleton.</summary>
public sealed class AgentRegistry
{
    public const string OfflineMessage = "server is offline";
    /// <summary>The result of a command nobody waits for any more (the caller went away or the time ran out).</summary>
    public const string CancelledMessage = "cancelled";

    private readonly ConcurrentDictionary<int, AgentSession> _sessions = new();
    private readonly ILogger<AgentRegistry> _log;
    private readonly CancellationToken _stopping;

    public AgentRegistry(ILogger<AgentRegistry> log, IHostApplicationLifetime lifetime)
    {
        _log = log;
        _stopping = lifetime.ApplicationStopping;
    }

    /// <summary>
    /// Raised whenever an agent connects, disconnects, sends a heartbeat or its stored data changed (Notify). The flag
    /// is true for a heartbeat: live data only, nothing stored about the agent is different.
    /// </summary>
    public event Action<int, bool>? Changed;

    /// <summary>
    /// Tells every subscriber of <see cref="Changed"/>, each on its own: a heartbeat raises it on the agent's stream,
    /// and one subscriber that throws must neither end that stream nor keep the others from hearing it.
    /// </summary>
    private void Raise(int agentId, bool heartbeat)
    {
        if (Changed is not { } changed) return;
        foreach (var handler in changed.GetInvocationList().Cast<Action<int, bool>>())
        {
            try { handler(agentId, heartbeat); }
            catch (Exception ex) { _log.LogError(ex, "{Subscriber} failed on a change of agent {Agent}", handler.Method.DeclaringType?.Name, agentId); }
        }
    }

    public IReadOnlyCollection<AgentSession> All => _sessions.Values.ToList();
    public AgentSession? Get(int agentId) => _sessions.GetValueOrDefault(agentId);

    /// <summary>A container from the agent's last heartbeat.</summary>
    public ContainerInfo? FindContainer(int agentId, string containerId) =>
        Get(agentId)?.Last?.Containers.FirstOrDefault(c => c.Id == containerId);

    public void Register(AgentSession s)
    {
        if (_sessions.TryRemove(s.AgentId, out var old))
        {
            _log.LogInformation("agent {name} reconnected, dropping previous session", s.Name);
            old.Cts.Cancel();
            FailAll(old, "agent reconnected");
        }
        _sessions[s.AgentId] = s;
        Raise(s.AgentId, false);
    }

    public void Unregister(AgentSession s)
    {
        // remove only this session: a reconnect may already have put the new one under the same key
        if (_sessions.TryRemove(new KeyValuePair<int, AgentSession>(s.AgentId, s)))
        {
            FailAll(s, "agent disconnected");
            Raise(s.AgentId, false);
        }
    }

    private static void FailAll(AgentSession s, string reason)
    {
        foreach (var p in s.Pending.Values)
            p.Tcs.TrySetResult(new CommandResult { Ok = false, Message = reason });
        s.Pending.Clear();
    }

    /// <summary>Tells listeners (the UI broadcaster) that stored agent data changed, e.g. after a rename.</summary>
    public void Notify(int agentId) => Raise(agentId, false);

    public void OnHeartbeat(AgentSession s, Heartbeat hb)
    {
        s.Last = hb;
        Raise(s.AgentId, true);
    }

    public void OnProgress(AgentSession s, CommandProgress p)
    {
        if (s.Pending.TryGetValue(p.CommandId, out var cmd)) cmd.OnProgress?.Invoke(p.Line);
    }

    public void OnLog(AgentSession s, LogChunk chunk)
    {
        if (s.Pending.TryGetValue(chunk.CommandId, out var cmd)) cmd.OnLog?.Invoke(chunk);
    }

    public void OnData(AgentSession s, DataChunk chunk)
    {
        if (s.Pending.TryGetValue(chunk.CommandId, out var cmd)) cmd.OnData?.Invoke(chunk.Data.ToByteArray());
    }

    public void OnDataAck(AgentSession s, DataAck ack)
    {
        if (ack.Chunks > 0 && s.Windows.TryGetValue(ack.CommandId, out var window)) window.Free.Release(ack.Chunks);
    }

    public void OnResult(AgentSession s, CommandResult r)
    {
        if (s.Pending.TryRemove(r.CommandId, out var cmd)) cmd.Tcs.TrySetResult(r);
    }

    /// <summary>
    /// Sends bytes to a running import, in order. An agent that acks them has at most DataWindow.Chunks it has not
    /// written yet: this waits for its acks (an older agent gets them as they come). False when the import is over or
    /// the connection is gone: nothing was sent.
    /// </summary>
    public async Task<bool> SendDataAsync(AgentSession s, DataChunk chunk, CancellationToken ct)
    {
        if (s.DataAcks)
        {
            if (!s.Windows.TryGetValue(chunk.CommandId, out var window)) return false;
            if (!chunk.Eof)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, s.Cts.Token, window.Over.Token);
                try { await window.Free.WaitAsync(wait.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
            }
        }
        try
        {
            await s.Send(new ServerMessage { Data = chunk }, ct);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogDebug("bytes for {Command} not sent: {Error}", chunk.CommandId, ex.Message);
            return false;
        }
    }

    /// <summary>Tells the agent a chunk of its export was passed on, when it waits for that; best effort.</summary>
    public async Task AckAsync(int agentId, string commandId)
    {
        if (Get(agentId) is not { DataAcks: true } s) return;
        try { await s.Send(new ServerMessage { DataAck = new DataAck { CommandId = commandId, Chunks = 1 } }); }
        catch (Exception ex) { _log.LogDebug("ack of {Command} not sent: {Error}", commandId, ex.Message); }
    }

    /// <summary>Sends a command and waits for its result. Progress lines and log chunks are delivered through callbacks.</summary>
    public async Task<CommandResult> SendAsync(int agentId, Command cmd, Action<string>? onProgress = null, Action<LogChunk>? onLog = null, CancellationToken ct = default,
        Action<byte[]>? onData = null)
    {
        var s = Get(agentId);
        if (s == null) return new CommandResult { Ok = false, Message = OfflineMessage };
        if (string.IsNullOrEmpty(cmd.Id)) cmd.Id = Guid.NewGuid().ToString("N");
        var pending = new PendingCommand { OnProgress = onProgress, OnLog = onLog, OnData = onData };
        s.Pending[cmd.Id] = pending;
        // an import's bytes wait for the agent's acks (SendDataAsync) until its result is in
        var window = s.DataAcks && cmd.BodyCase == Command.BodyOneofCase.ImportVolume ? s.Windows[cmd.Id] = new DataWindow() : null;
        try
        {
            try
            {
                await s.Send(new ServerMessage { Command = cmd }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // still waiting for the stream (another message to a slow agent): nothing went out
                s.Pending.TryRemove(cmd.Id, out _);
                return new CommandResult { Ok = false, Message = CancelledMessage };
            }
            catch (Exception ex)
            {
                s.Pending.TryRemove(cmd.Id, out _);
                return new CommandResult { Ok = false, Message = "send failed: " + ex.Message };
            }
            using var reg = ct.Register(() =>
            {
                if (!pending.Tcs.TrySetResult(new CommandResult { Ok = false, Message = CancelledMessage })) return;
                // A shutdown aborts every request, but the command must go on: when an agent re-creates the Dockmaster
                // container itself, its stop is what shuts this server down, and a cancel from here left it stopped.
                if (_stopping.IsCancellationRequested) return;
                // nobody waits any more: let the agent stop too (follow-logs would otherwise stream forever)
                _ = s.Send(new ServerMessage { Cancel = new CancelCommand { CommandId = cmd.Id } }).ContinueWith(_ => { }, TaskScheduler.Default);
            });
            var result = await pending.Tcs.Task;
            s.Pending.TryRemove(cmd.Id, out _);
            return result;
        }
        finally
        {
            if (window != null && s.Windows.TryRemove(cmd.Id, out _)) window.Over.Cancel();
        }
    }

    /// <summary>Sends a command with a time limit (and stops waiting when the caller goes away).</summary>
    public async Task<CommandResult> SendAsync(int agentId, Command cmd, TimeSpan timeout, CancellationToken ct, Action<string>? onProgress = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var r = await SendAsync(agentId, cmd, onProgress, ct: cts.Token);
        if (!r.Ok && r.Message == CancelledMessage && !ct.IsCancellationRequested) r.Message = $"the agent did not answer within {timeout.TotalSeconds:0}s";
        return r;
    }

    /// <summary>The result text for a person; an agent that predates the command answers "unknown command".</summary>
    public static string ErrorText(CommandResult r) =>
        !r.Ok && r.Message == WireText.UnknownCommand ? "the agent is too old for this — update it" : r.Message;
}
