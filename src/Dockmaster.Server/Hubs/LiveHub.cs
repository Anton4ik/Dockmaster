using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Threading.Channels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Protocol;
using Dockmaster.Server.Access;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Server.Hubs;

/// <summary>
/// Browser ↔ server realtime channel.
/// Server → client: "agent" (AgentDto on every heartbeat) and "containers" (agentId, ContainerDto[]) for watched agents.
/// Client → server: streams for deploys, re-creates, project actions and container logs.
/// Every call checks the caller's permissions (<see cref="AccessService"/>) on the servers it touches; a refused
/// command ends with the result marker and the reason, a refused plain call throws.
/// </summary>
[Authorize]
public sealed class LiveHub : Hub
{
    /// <summary>Marks the last line of a command stream: "\u0001ok message" or "\u0001fail message".</summary>
    private const char ResultMarker = '\u0001';
    /// <summary>Marks the first line of a command stream: "\u0002runId", what <see cref="CancelRun"/> takes.</summary>
    private const char RunMarker = '\u0002';

    /// <summary>Marks the first item of a terminal stream: "\u0003session", what <see cref="ExecInput"/> takes.</summary>
    private const char SessionMarker = '\u0003';
    /// <summary>Marks every piece of a terminal's output, so output never passes for a marker: "\u0004text".</summary>
    private const char DataMarker = '\u0004';
    // pieces of output waiting for a slow browser; past this the stream ends instead of growing in memory. A command's
    // lines are short and a pull prints many at once: more of them wait
    private const int StreamBuffer = 1000;
    private const int CommandBuffer = 50_000;

    // commands running now, by run id, with the user who started them: they belong to the server, not to the stream that started them
    private static readonly ConcurrentDictionary<string, (CancellationTokenSource Cts, int UserId)> Runs = new();
    // terminals open now, by session: the server they run on and the browser connection that may type into them
    private static readonly ConcurrentDictionary<string, (int AgentId, string ConnectionId)> Terminals = new();

    private readonly AgentRegistry _agents;
    private readonly ProjectService _projects;
    private readonly ImageUpdateChecker _updates;
    private readonly MeshService _mesh;
    private readonly RunLogs _runLogs;
    private readonly Watchers _watchers;
    private readonly IDbContextFactory<DockmasterDb> _dbf;
    private readonly AccessService _access;
    private readonly Notifier _notifier;

    public LiveHub(AgentRegistry agents, ProjectService projects, ImageUpdateChecker updates, MeshService mesh, RunLogs runLogs, Watchers watchers,
        IDbContextFactory<DockmasterDb> dbf, AccessService access, Notifier notifier)
    {
        _access = access;
        _notifier = notifier;
        _agents = agents;
        _projects = projects;
        _updates = updates;
        _mesh = mesh;
        _runLogs = runLogs;
        _watchers = watchers;
        _dbf = dbf;
    }

    public override async Task OnConnectedAsync()
    {
        // kept to be ended when the user's access changes: the browser connects again and gets what it may see now
        if (await _access.ForAsync(Context.User!) is not { } a)
        {
            Context.Abort();
            return;
        }
        _access.Connected(a.UserId, Context);
        _watchers.Connected(Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _access.Disconnected(Context.ConnectionId);
        _watchers.Disconnected(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>The caller's access; a session that ended meanwhile (password changed, account disabled) gets nothing.</summary>
    private static async Task<UserAccess> AccessOf(AccessService access, ClaimsPrincipal? user) =>
        user != null && await access.ForAsync(user) is { } a ? a : throw new HubException("not signed in");

    // each check gives back the caller's access: who did it goes into the project's history and the events

    private static async Task<UserAccess> RequireAdmin(AccessService access, ClaimsPrincipal? user)
    {
        var a = await AccessOf(access, user);
        return a.IsAdmin ? a : throw new HubException("only an admin may do this");
    }

    private static async Task<UserAccess> RequireServer(AccessService access, ClaimsPrincipal? user, int agentId, string perm)
    {
        var a = await AccessOf(access, user);
        if (!a.Sees(agentId)) throw new HubException("server not found");
        if (!a.Can(agentId, perm)) throw new HubException($"you have no {AccessService.Label(perm)} permission on this server");
        return a;
    }

    private static async Task<UserAccess> RequireProject(AccessService access, ClaimsPrincipal? user, int projectId, params string[] perms)
    {
        var a = await AccessOf(access, user);
        var (found, denied) = await access.ProjectDeniedAsync(a, projectId, perms);
        if (!found) throw new HubException("project not found");
        if (denied != null) throw new HubException(denied);
        return a;
    }

    /// <summary>
    /// Puts a server into a network (joining the mesh first when needed: keys, sidecar, configs for everyone),
    /// streaming what happens. No network given: the only one, or a new "default".
    /// </summary>
    public IAsyncEnumerable<string> MeshJoin(int agentId, int? networkId, CancellationToken ct) =>
        Command(RequireAdmin(_access, Context.User), (_, line, token) => _mesh.AddToNetworkAsync(agentId, networkId, line, token), ct);

    public IAsyncEnumerable<string> MeshLeave(int agentId, CancellationToken ct) =>
        Command(RequireAdmin(_access, Context.User), (_, line, token) => _mesh.LeaveAsync(agentId, line, token), ct);

    /// <summary>iperf3 between two servers over the mesh.</summary>
    public IAsyncEnumerable<string> MeshBench(int fromId, int toId, CancellationToken ct) =>
        Command(RequireAdmin(_access, Context.User), (_, line, token) => _mesh.BenchAsync(fromId, toId, line, token), ct);

    /// <summary>Every host measures its paths and gets the tunnel MTU they carry (Tune speed).</summary>
    public IAsyncEnumerable<string> MeshTune(CancellationToken ct) => Command(RequireAdmin(_access, Context.User), (_, line, token) => _mesh.TuneAsync(line, token), ct);

    public static string AgentGroup(int agentId) => $"agent-{agentId}";

    /// <summary>The page shows this server's containers: they are pushed on every heartbeat, and it reports more often.</summary>
    public async Task WatchAgent(int agentId)
    {
        await RequireServer(_access, Context.User, agentId, Perm.View);
        await Groups.AddToGroupAsync(Context.ConnectionId, AgentGroup(agentId));
        _watchers.Watch(Context.ConnectionId, agentId);
    }

    /// <summary>The tab went to the background or came back: a hidden tab does not make the servers report more often.</summary>
    public void SetHidden(bool hidden) => _watchers.SetHidden(Context.ConnectionId, hidden);

    public async Task UnwatchAgent(int agentId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, AgentGroup(agentId));
        _watchers.Unwatch(Context.ConnectionId, agentId);
    }

    /// <summary>Deploys a project (or some of its services) and streams the log lines.</summary>
    public IAsyncEnumerable<string> Deploy(int projectId, string[]? services, bool pull, CancellationToken ct) =>
        Command(RequireProject(_access, Context.User, projectId, Perm.Deploy),
            (who, line, token) => Audited(who, $"deploy{List(services)}{(pull ? " with pull" : "")}", projectId,
                _projects.DeployAsync(projectId, services is { Length: > 0 } ? services : null, pull, false, line, token, who.Name)), ct);

    /// <summary>
    /// Puts the files of an earlier deploy back and deploys them, streaming the log lines. It writes the .env, so it
    /// takes Secrets too; and the servers of then, which may not be the servers of now, need the permissions as well.
    /// </summary>
    public IAsyncEnumerable<string> Rollback(int projectId, int deploymentId, CancellationToken ct) =>
        Command(RequireProject(_access, Context.User, projectId, Perm.Deploy, Perm.Secrets), async (who, line, token) =>
        {
            if (await _access.RollbackDeniedAsync(who, projectId, deploymentId, token) is { } denied) throw new HubException(denied);
            return await Audited(who, $"rollback to #{deploymentId}", projectId, _projects.RollbackAsync(projectId, deploymentId, line, token, who.Name));
        }, ct);

    /// <summary>
    /// Pull and re-create one container. Containers of Dockmaster projects are deployed through their project
    /// (only that service, rolling update replica by replica when it has several); others are re-created by the agent.
    /// </summary>
    public IAsyncEnumerable<string> RecreateContainer(int agentId, string containerId, bool pull, CancellationToken ct)
    {
        var user = Context.User;
        return Command(RequireServer(_access, user, agentId, Perm.Deploy), async (who, line, token) =>
        {
            var c = _agents.FindContainer(agentId, containerId) ?? throw new HubException("container not found (server offline?)");
            if (c.Service.Length > 0 && await _projects.FindAsync(agentId, c.Project, token) is { } projectId)
            {
                // deployed through its project: on every server the project is on
                await RequireProject(_access, user, projectId, Perm.Deploy);
                line($"{c.Name} belongs to project {c.Project}: deploying service {c.Service}{(pull ? " with pull" : "")}");
                return await Audited(who, $"re-create container {c.Name}{(pull ? " with pull" : "")}", projectId,
                    _projects.DeployAsync(projectId, [c.Service], pull, false, line, token, who.Name), c.Name);
            }
            var cmd = new RecreateContainer { ContainerId = containerId, Pull = pull };
            cmd.Registries.AddRange(await _projects.CredentialsForAsync(c.ImageRef, token));
            var r = await _agents.SendAsync(agentId, new Command { RecreateContainer = cmd }, onProgress: line, ct: token);
            if (r.Ok && pull) _updates.RecheckInBackground(agentId, [c.ImageRef]);
            // not a project's: there is no history for it, the events have it
            await _notifier.AuditAsync(who, $"re-created container {c.Name}{(pull ? " with pull" : "")}{(r.Ok ? "" : " — failed: " + AgentRegistry.ErrorText(r))}",
                agentId: agentId, container: c.Name);
            return (r.Ok, AgentRegistry.ErrorText(r));
        }, ct);
    }

    /// <summary>start / stop / restart / down for a project (or some services) with streamed output.</summary>
    public IAsyncEnumerable<string> ProjectAction(int projectId, string op, string[]? services, CancellationToken ct)
    {
        var pop = op.ToLowerInvariant() switch
        {
            "start" => ProjectOp.ProjectStart,
            "stop" => ProjectOp.ProjectStop,
            "restart" => ProjectOp.ProjectRestart,
            "down" => ProjectOp.ProjectDown,
            _ => throw new HubException($"unknown action '{op}'"),
        };
        return Command(RequireProject(_access, Context.User, projectId, Perm.Control),
            (who, line, token) => Audited(who, op.ToLowerInvariant() + List(services), projectId,
                _projects.ActionAsync(projectId, pop, services is { Length: > 0 } ? services : null, line, token, who.Name)), ct);
    }

    /// <summary>
    /// A project run's log, from its start: what it printed so far, then each line as it comes, then its outcome — for a
    /// run started from a page that was left since. A finished run comes from the history.
    /// </summary>
    public async IAsyncEnumerable<string> FollowDeployment(int projectId, int deploymentId, [EnumeratorCancellation] CancellationToken ct)
    {
        string? refused = null;
        try { await RequireProject(_access, Context.User, projectId, Perm.View); }
        catch (HubException ex) { refused = ex.Message; }
        if (refused != null) { yield return $"{ResultMarker}fail {refused}"; yield break; }
        // the run must be one of this project's: a deployment id alone would show any project's log
        await using (var check = await _dbf.CreateDbContextAsync(ct))
            if (await check.Deployments.Where(d => d.Id == deploymentId).Select(d => (int?)d.ProjectId).FirstOrDefaultAsync(ct) is { } of && of != projectId) { yield return $"{ResultMarker}fail no such deployment"; yield break; }
        if (_runLogs.Watch(deploymentId) is { } run)
        {
            try
            {
                foreach (var line in Lines(run.SoFar)) yield return line;
                await foreach (var line in run.Next.ReadAllAsync(ct)) yield return line;
                var (ok, message) = await run.Result.WaitAsync(ct);
                yield return $"{ResultMarker}{(ok ? "ok" : "fail")} {message}";
            }
            finally { run.Stop(); }
            yield break;
        }
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var d = await db.Deployments.FirstOrDefaultAsync(x => x.Id == deploymentId && x.ProjectId == projectId, ct);
        if (d == null) { yield return $"{ResultMarker}fail no such deployment"; yield break; }
        foreach (var line in Lines(d.Log)) yield return line;
        // not running and never finished: the server went away while it ran
        yield return d.Ok is { } done ? $"{ResultMarker}{(done ? "ok" : "fail")} {d.Message}" : $"{ResultMarker}fail not running any more — the server restarted while it ran";
    }

    private static IEnumerable<string> Lines(string text) => text.Split('\n').Select(l => l.TrimEnd('\r')).SkipLast(text.EndsWith('\n') ? 1 : 0);

    /// <summary>Tails and follows a container's logs until the client cancels the stream.</summary>
    public IAsyncEnumerable<string> ContainerLogs(int agentId, string containerId, int tail, CancellationToken ct)
    {
        var allowed = RequireServer(_access, Context.User, agentId, Perm.View);
        return Pump(async (write, token) =>
        {
            await allowed;
            var cmd = new Command { ContainerLogs = new ContainerLogs { ContainerId = containerId, Tail = tail, Follow = true } };
            var r = await _agents.SendAsync(agentId, cmd, onLog: chunk => { if (!chunk.Eof) write(chunk.Text); }, ct: token);
            if (!r.Ok && !token.IsCancellationRequested) write($"\n[{r.Message}]\n");
        }, ex => $"\n[{ex.Message}]\n", ct);
    }

    /// <summary>
    /// A shell in a container, for the browser's terminal: the session marker first, then what the shell prints (each
    /// piece after the data marker), then the result marker when it exits. Leaving the page (the stream ends) closes the shell.
    /// </summary>
    public IAsyncEnumerable<string> Exec(int agentId, string container, int cols, int rows, CancellationToken ct)
    {
        var connection = Context.ConnectionId;
        var agents = _agents;
        var allowed = RequireServer(_access, Context.User, agentId, Perm.Terminal);
        return Pump(async (write, token) =>
        {
            await allowed;
            var id = Guid.NewGuid().ToString("N");
            Terminals[id] = (agentId, connection);
            try
            {
                write($"{SessionMarker}{id}");
                var cmd = new Command { Id = id, StartExec = new StartExec { Container = container, Cols = cols, Rows = rows } };
                var r = await agents.SendAsync(agentId, cmd, onLog: chunk => { if (!chunk.Eof && chunk.Text.Length > 0) write(DataMarker + chunk.Text); }, ct: token);
                write($"{ResultMarker}{(r.Ok ? "ok" : "fail")} {AgentRegistry.ErrorText(r)}");
            }
            finally { Terminals.TryRemove(id, out _); }
        }, ex => $"{ResultMarker}fail {ex.Message}", ct);
    }

    /// <summary>
    /// A project run with its outcome as an audit event naming who started it (the project's history has it too): the
    /// Events page's what-people-did has the deploys next to the changes. A run that throws (cancelled while it waited
    /// for the project, the project gone) is in the events too.
    /// </summary>
    private async Task<(bool ok, string message)> Audited(UserAccess who, string what, int projectId, Task<(bool ok, string message)> run, string? container = null)
    {
        (bool ok, string message) result;
        try { result = await run; }
        catch (Exception ex)
        {
            await _notifier.AuditAsync(who, $"{what} — {(ex is OperationCanceledException ? "cancelled" : "failed: " + ex.Message)}", projectId, container: container);
            throw;
        }
        await _notifier.AuditAsync(who, result.ok ? what : $"{what} — failed: {result.message}", projectId, container: container);
        return result;
    }

    private static string List(string[]? services) => services is { Length: > 0 } ? " " + string.Join(", ", services) : "";

    /// <summary>Keys typed into a terminal and its size (0 = unchanged); only from the connection that opened it.</summary>
    public async Task ExecInput(string session, string data, int cols, int rows)
    {
        if (!Terminals.TryGetValue(session, out var t) || t.ConnectionId != Context.ConnectionId) return;
        if (_agents.Get(t.AgentId) is not { } s) return;
        await s.Send(new ServerMessage { ExecInput = new ExecInput { Session = session, Data = Google.Protobuf.ByteString.CopyFromUtf8(data ?? ""), Cols = cols, Rows = rows } });
    }

    /// <summary>
    /// A command whose progress lines stream to the browser, started by the run marker line and ended by the result
    /// marker line. The command runs to its end also when the browser goes away (page left, tab closed): leaving only
    /// stops the watching, a deploy cut halfway would leave the services half done. <see cref="CancelRun"/> stops it.
    /// </summary>
    /// <param name="allowed">the permission check, started when the call came: a refusal ends the stream with its reason; the work gets who passed it</param>
    private IAsyncEnumerable<string> Command(Task<UserAccess> allowed, Func<UserAccess, Action<string>, CancellationToken, Task<(bool ok, string message)>> work, CancellationToken ct)
    {
        var runId = Guid.NewGuid().ToString("N");
        var run = new CancellationTokenSource();
        var userId = int.TryParse(Context.UserIdentifier, out var uid) ? uid : 0;
        Runs[runId] = (run, userId);
        return Pump(async (write, streamToken) => // not streamToken: the run outlives the stream
        {
            UserAccess who;
            try { who = await allowed; }
            catch
            {
                Runs.TryRemove(runId, out _);
                throw;
            }
            write($"{RunMarker}{runId}");
            try
            {
                var (ok, message) = await work(who, write, run.Token);
                write($"{ResultMarker}{(ok ? "ok" : "fail")} {message}");
            }
            finally { Runs.TryRemove(runId, out _); }
        }, ex => $"{ResultMarker}fail {ex.Message}", ct, CommandBuffer);
    }

    /// <summary>Stops a running command (the cancel button); unknown or finished runs are ignored.</summary>
    public async Task CancelRun(string runId)
    {
        if (!Runs.TryGetValue(runId, out var run)) return;
        // the one who started it, or an admin
        if (run.UserId.ToString() != Context.UserIdentifier && !(await AccessOf(_access, Context.User)).IsAdmin) throw new HubException("only who started it, or an admin, may stop it");
        run.Cts.Cancel();
    }

    /// <summary>
    /// Runs work that writes text, and yields it as it comes until the work ends or the client goes away. The work gets
    /// the stream's token: work that must outlive the client (<see cref="Command"/>) uses its own. A client that reads
    /// slower than the work writes (`yes` in a terminal) is not buffered for without end: the stream stops with a
    /// message and the work is cancelled.
    /// </summary>
    private static async IAsyncEnumerable<string> Pump(Func<Action<string>, CancellationToken, Task> work, Func<Exception, string> onError,
        [EnumeratorCancellation] CancellationToken ct, int capacity = StreamBuffer)
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity) { SingleReader = true });
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var done = false;
        var overflow = 0;
        void Write(string text)
        {
            if (channel.Writer.TryWrite(text) || Volatile.Read(ref done) || Interlocked.Exchange(ref overflow, 1) == 1) return;
            channel.Writer.TryComplete();
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        var task = Task.Run(async () =>
        {
            try { await work(Write, cts.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException || !cts.IsCancellationRequested) { channel.Writer.TryWrite(onError(ex)); }
            catch (OperationCanceledException) { }
            finally
            {
                Volatile.Write(ref done, true);
                channel.Writer.TryComplete();
                cts.Dispose();
            }
        }, CancellationToken.None);
        await foreach (var text in channel.Reader.ReadAllAsync(ct)) yield return text;
        if (Volatile.Read(ref overflow) == 1)
        {
            yield return onError(new IOException("output too fast for the browser — stopped"));
            yield break;
        }
        await task;
    }
}
