using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Dockmaster.Protocol;

namespace Dockmaster.Agent.Docker;

/// <summary>
/// Shells in containers for the browser's terminal: `docker exec -it` with a TTY. What the shell prints goes to the
/// server as log chunks of the command that started it; keys and window sizes come back as ExecInput messages.
/// </summary>
public sealed class ExecStore
{
    // keys a shell does not read wait here; past these limits the session ends instead of holding the agent
    private const int InputQueueMessages = 1024;
    private const long InputQueueBytes = 4 * 1024 * 1024;
    // input for a session nobody started (it ended, or never came) is dropped after this
    private static readonly TimeSpan Unclaimed = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ResizeTimeout = TimeSpan.FromSeconds(5);
    // keys typed before the shell printed its pid wait at most this long
    private static readonly TimeSpan PidWait = TimeSpan.FromSeconds(2);

    /// <summary>The shell's pid, printed first as an OSC sequence terminals ignore: ESC ] 7777 ; pid BEL.</summary>
    private const string PidPrefix = "\u001b]7777;";

    private readonly DockerService _docker;
    private readonly EngineApi _api;
    private readonly ILogger<ExecStore> _log;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    public ExecStore(DockerService docker, ILogger<ExecStore> log)
    {
        _docker = docker;
        _api = docker.Api;
        _log = log;
    }

    private sealed class Session
    {
        public readonly Channel<ExecInput> Input = Channel.CreateBounded<ExecInput>(new BoundedChannelOptions(InputQueueMessages) { SingleReader = true });
        // cancelled when the input overflows
        public readonly CancellationTokenSource Ended = new();
        public readonly long CreatedMs = Environment.TickCount64;
        public long QueuedBytes;
        public volatile bool Started;
    }

    /// <summary>bash where the image has it, sh otherwise; the pid first, so a closed terminal can end it.</summary>
    private static readonly string[] Shell = ["/bin/sh", "-c", "printf \"\\033]7777;%s\\007\" $$; if command -v bash >/dev/null 2>&1; then exec bash; else exec sh; fi"];

    /// <summary>Runs the shell until it exits or the command is cancelled; the result says how it ended.</summary>
    public async Task<string> RunAsync(string commandId, StartExec req, Func<AgentMessage, Task> send, CancellationToken ct)
    {
        // before the exec exists: keys typed while it starts wait in the queue
        var s = _sessions.GetOrAdd(commandId, _ => new Session());
        s.Started = true;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, s.Ended.Token);
        string? execId = null;
        DockerStream? stream = null;
        var writer = Task.CompletedTask;
        int? pid = null;
        var eof = false;
        try
        {
            if (await _docker.GuardedAsync(req.Container, cts.Token) is { } guarded) throw new InvalidOperationException($"{guarded}: no terminal in it");
            // the size comes as the terminal's own, never as COLUMNS / LINES: busybox's tools and shell would keep using
            // those over every later resize
            execId = await _api.CreateExecAsync(req.Container, Shell, stdin: true, tty: true, ["TERM=xterm-256color"], cts.Token,
                (Math.Max(5, req.Rows), Math.Max(20, req.Cols)));
            stream = await _api.StartExecAsync(execId, true, cts.Token);
            await ResizeAsync(execId, req.Cols, req.Rows);
            var pidRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            writer = WriteInputAsync(s, execId, stream, pidRead.Task, cts);
            // a UTF-8 character may be split over two reads: the decoder keeps the half until the rest comes
            var decoder = Encoding.UTF8.GetDecoder();
            var buffer = new byte[8192];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
            // the output until the pid is read off its start; null after
            string? head = "";
            while (!cts.IsCancellationRequested)
            {
                var r = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cts.Token);
                if (r.EOF) { eof = true; break; }
                var text = new string(chars, 0, decoder.GetChars(buffer, 0, r.Count, chars, 0));
                if (head != null)
                {
                    head += text;
                    if (!TryTakePid(head, out pid, out text)) continue;
                    head = null;
                    pidRead.TrySetResult();
                }
                if (text.Length > 0) await send(new AgentMessage { Logs = new LogChunk { CommandId = commandId, ContainerId = req.Container, Text = text } });
            }
            if (!string.IsNullOrEmpty(head)) await send(new AgentMessage { Logs = new LogChunk { CommandId = commandId, ContainerId = req.Container, Text = head } });
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        finally
        {
            _sessions.TryRemove(new KeyValuePair<string, Session>(commandId, s));
            s.Input.Writer.TryComplete();
            cts.Cancel();
            // unblocks a write the shell does not read
            stream?.Dispose();
            try { await writer; } catch { }
        }
        try { await send(new AgentMessage { Logs = new LogChunk { CommandId = commandId, ContainerId = req.Container, Eof = true } }); } catch { }
        if (execId == null) return "closed";
        try
        {
            var info = await _api.InspectExecAsync(execId, CancellationToken.None);
            if (!info.Running) return $"exited with code {info.ExitCode}";
            if (!eof && pid is { } p) await EndShellAsync(req.Container, p);
            return "closed";
        }
        catch (Exception ex)
        {
            _log.LogDebug("exec inspect: {Error}", ex.Message);
            return "closed";
        }
    }

    /// <summary>Keys typed in the browser or a new window size: only queued for the session, never waits and never throws.</summary>
    public Task InputAsync(ExecInput input)
    {
        try
        {
            DropUnclaimed();
            var s = _sessions.GetOrAdd(input.Session, _ => new Session());
            if (s.Ended.IsCancellationRequested) return Task.CompletedTask;
            if (Interlocked.Add(ref s.QueuedBytes, input.Data.Length) > InputQueueBytes || !s.Input.Writer.TryWrite(input))
            {
                _log.LogWarning("terminal {Session}: the shell does not read what is typed, closing it", input.Session);
                s.Input.Writer.TryComplete();
                s.Ended.Cancel();
            }
        }
        catch (Exception ex) { _log.LogDebug("exec input: {Error}", ex.Message); }
        return Task.CompletedTask;
    }

    private void DropUnclaimed()
    {
        var now = Environment.TickCount64;
        foreach (var (id, s) in _sessions)
            if (!s.Started && now - s.CreatedMs > Unclaimed.TotalMilliseconds) _sessions.TryRemove(new KeyValuePair<string, Session>(id, s));
    }

    /// <summary>Writes the queued input into the shell, one session each: a shell that does not read blocks only itself.</summary>
    private async Task WriteInputAsync(Session s, string execId, DockerStream stream, Task pidRead, CancellationTokenSource cts)
    {
        try
        {
            // keys typed early would be echoed before the pid
            await Task.WhenAny(pidRead, Task.Delay(PidWait, cts.Token));
            await foreach (var input in s.Input.Reader.ReadAllAsync(cts.Token))
            {
                Interlocked.Add(ref s.QueuedBytes, -input.Data.Length);
                if (input.Data.Length > 0)
                {
                    var bytes = input.Data.ToByteArray();
                    await stream.WriteAsync(bytes, 0, bytes.Length, cts.Token);
                }
                if (input.Cols > 0 && input.Rows > 0) await ResizeAsync(execId, input.Cols, input.Rows);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.LogDebug("exec input: {Error}", ex.Message);
            cts.Cancel();
        }
    }

    private async Task ResizeAsync(string execId, int cols, int rows)
    {
        if (cols <= 0 || rows <= 0) return;
        using var timeout = new CancellationTokenSource(ResizeTimeout);
        try { await _api.ResizeExecAsync(execId, cols, rows, timeout.Token); }
        catch (Exception ex) { _log.LogDebug("exec resize: {Error}", ex.Message); }
    }

    /// <summary>
    /// Docker leaves a TTY exec running when its stream closes: the shell is hung up (taking what runs in it along),
    /// then killed. Best effort, in a second exec.
    /// </summary>
    private async Task EndShellAsync(string container, int pid)
    {
        try { await _docker.ExecAsync(container, $"kill -HUP {pid}; sleep 2; kill -KILL {pid} 2>/dev/null", TimeSpan.FromSeconds(10), CancellationToken.None); }
        catch (Exception ex) { _log.LogWarning("closing shell {Pid} in {Container}: {Error}", pid, container, ex.Message); }
    }

    /// <summary>
    /// Reads the pid off the start of a shell's output. False while the output so far may still become it; then the pid
    /// (null when the output does not start with it) and the output after it.
    /// </summary>
    public static bool TryTakePid(string head, out int? pid, out string rest)
    {
        pid = null;
        rest = head;
        if (head.Length < PidPrefix.Length) return !PidPrefix.StartsWith(head, StringComparison.Ordinal);
        if (!head.StartsWith(PidPrefix, StringComparison.Ordinal)) return true;
        var end = head.IndexOf('\a', PidPrefix.Length);
        if (end < 0) return head.Length > PidPrefix.Length + 12;
        if (int.TryParse(head.AsSpan(PidPrefix.Length, end - PidPrefix.Length), out var p)) pid = p;
        rest = head[(end + 1)..];
        return true;
    }
}
