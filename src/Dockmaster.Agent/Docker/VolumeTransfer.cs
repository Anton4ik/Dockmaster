using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Google.Protobuf;
using Dockmaster.Protocol;

namespace Dockmaster.Agent.Docker;

/// <summary>
/// What is inside a volume, taken out or put in: a short-lived helper container from the agent's own image mounts the
/// volume and runs tar (or the agent itself, to list a folder). The bytes travel as DataChunk messages, so a volume can
/// be downloaded, uploaded, or copied to another server through the Dockmaster server.
/// </summary>
public sealed class VolumeTransfer
{
    public const string ListDirArg = "list-dir";
    // chunks of an export the server has not acked yet, at most (when it acks them)
    private const int Window = 32;
    // an import is unpacked here first, inside the volume: moving it in afterwards is a rename, not a copy
    private const string Incoming = "/v/.dockmaster-incoming";

    private readonly VolumeStore _volumes;
    private readonly ILogger<VolumeTransfer> _log;
    // imports by command id: the first bytes may arrive before the command itself has started
    private readonly ConcurrentDictionary<string, Channel<DataChunk>> _imports = new();
    // imports that are over: bytes still on the way for them are dropped instead of starting a new buffer
    private readonly ConcurrentDictionary<string, DateTime> _finished = new();
    // exports by command id: the chunks they may still send before the server's next ack
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _windows = new();

    public VolumeTransfer(VolumeStore volumes, ILogger<VolumeTransfer> log)
    {
        _volumes = volumes;
        _log = log;
    }

    /// <summary>A path inside the volume, never outside it: "a/b" → "/v/a/b".</summary>
    private static string Inside(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is ".." or ".")) throw new ArgumentException("the path cannot contain . or ..");
        return "/v" + (parts.Length > 0 ? "/" + string.Join('/', parts) : "");
    }

    public async Task<string> ExportAsync(string commandId, ExportVolume req, Func<AgentMessage, Task> send, CancellationToken ct)
    {
        var at = Inside(req.Path);
        string[] cmd = req.File ? ["cat", at] : ["tar", "-czf", "-", "-C", at, "."];
        long total = 0;
        // a server that acks the chunks is never more than a window behind; an older one gets them as they come
        var window = req.Acks ? new SemaphoreSlim(Window) : null;
        if (window != null) _windows[commandId] = window;
        try
        {
            var (code, errors) = await _volumes.HelperAsync(req.Volume, true, cmd, null, null, async bytes =>
            {
                if (window != null) await window.WaitAsync(ct);
                total += bytes.Length;
                await send(new AgentMessage { Data = new DataChunk { CommandId = commandId, Data = ByteString.CopyFrom(bytes.Span) } });
            }, ct);
            if (code != 0) throw new InvalidOperationException($"reading {req.Volume}{(req.Path.Length > 0 ? "/" + req.Path.Trim('/') : "")} failed: {errors}");
            return $"{total} bytes";
        }
        finally
        {
            if (window != null) _windows.TryRemove(commandId, out _);
        }
    }

    /// <summary>The server passed chunks of an export on: as many more may go.</summary>
    public void Ack(DataAck ack)
    {
        if (ack.Chunks > 0 && _windows.TryGetValue(ack.CommandId, out var window)) window.Release(ack.Chunks);
    }

    /// <summary>
    /// Unpacks a tar.gz into the volume. It goes into a folder aside first; only once tar has taken all of it does it
    /// replace (wipe) or join what is there, so a broken or cut off archive leaves the volume as it was. A volume a
    /// running container uses is refused: the files would change under it.
    /// </summary>
    public async Task<string> ImportAsync(string commandId, ImportVolume req, Func<AgentMessage, Task> send, CancellationToken ct)
    {
        var channel = _imports.GetOrAdd(commandId, _ => NewBuffer());
        try
        {
            var running = (await _volumes.UsersOfAsync(req.Volume, ct)).Where(u => u.Running).Select(u => u.Name).ToList();
            if (running.Count > 0)
                throw new InvalidOperationException($"{string.Join(", ", running)} {(running.Count == 1 ? "uses" : "use")} {req.Volume} now: stop {(running.Count == 1 ? "it" : "them")} first");
            // each chunk written is acked: the server sends the next ones only then (an older server ignores the acks)
            async Task Written()
            {
                try { await send(new AgentMessage { DataAck = new DataAck { CommandId = commandId, Chunks = 1 } }); }
                catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogDebug("ack of {Command} not delivered: {Error}", commandId, ex.Message); }
            }
            long code;
            string errors;
            try { (code, errors) = await _volumes.HelperAsync(req.Volume, false, ["sh", "-c", ImportScript(req.Wipe)], channel.Reader, Written, null, ct); }
            catch
            {
                await DropIncomingAsync(req.Volume);
                throw;
            }
            if (code != 0)
            {
                await DropIncomingAsync(req.Volume);
                throw new InvalidOperationException($"unpacking into {req.Volume} failed, it is left as it was: {errors}");
            }
            return req.Wipe ? $"{req.Volume} replaced" : $"unpacked into {req.Volume}";
        }
        finally
        {
            _finished[commandId] = DateTime.UtcNow;
            channel.Writer.TryComplete();
            _imports.TryRemove(commandId, out _);
            foreach (var old in _finished.Where(x => x.Value < DateTime.UtcNow.AddHours(-1)).Select(x => x.Key).ToList()) _finished.TryRemove(old, out _);
        }
    }

    /// <summary>
    /// Unpacks stdin into Incoming (set -e: the first failure stops it), then empties the volume and moves the files in
    /// (wipe), or copies them over what is there. Plain sh and busybox: the agent's image is alpine.
    /// </summary>
    private static string ImportScript(bool wipe)
    {
        string[] into = wipe
            ? [
                // unmatched globs stay as they are: [ -e ] skips them
                "for f in /v/* /v/.[!.]* /v/..?*; do",
                "  if [ \"$f\" = \"$s\" ] || { [ ! -e \"$f\" ] && [ ! -L \"$f\" ]; }; then continue; fi",
                "  rm -rf \"$f\"",
                "done",
                "for f in \"$s\"/* \"$s\"/.[!.]* \"$s\"/..?*; do",
                "  if [ ! -e \"$f\" ] && [ ! -L \"$f\" ]; then continue; fi",
                "  mv \"$f\" /v/",
                "done",
                "rmdir \"$s\"",
            ]
            : ["cp -a \"$s\"/. /v/", "rm -rf \"$s\""];
        string[] lines = ["set -e", $"s={Incoming}", "rm -rf \"$s\"", "mkdir \"$s\"", "tar -xzf - -C \"$s\"", .. into];
        return string.Join('\n', lines);
    }

    /// <summary>Removes what a failed or cancelled import unpacked aside; best effort.</summary>
    private async Task DropIncomingAsync(string volume)
    {
        try
        {
            var (code, errors) = await _volumes.HelperAsync(volume, false, ["rm", "-rf", Incoming], null, null, null, CancellationToken.None);
            if (code != 0) _log.LogWarning("cleaning up {Volume} after an import failed: {Error}", volume, errors);
        }
        catch (Exception ex) { _log.LogWarning("cleaning up {Volume} after an import failed: {Error}", volume, ex.Message); }
    }

    // unbounded: the connection's read loop never waits for a helper (heartbeats and other commands share it); a server
    // that waits for acks keeps it short
    private static Channel<DataChunk> NewBuffer() => Channel.CreateUnbounded<DataChunk>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    /// <summary>Bytes for an import, in order (buffered until the helper takes them).</summary>
    public Task DataAsync(DataChunk chunk)
    {
        if (_finished.ContainsKey(chunk.CommandId)) return Task.CompletedTask;
        var channel = _imports.GetOrAdd(chunk.CommandId, _ => NewBuffer());
        channel.Writer.TryWrite(chunk);
        if (chunk.Eof) channel.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public sealed record Entry(string Name, bool Dir, long Size, long Modified, bool Link);

    public async Task<string> BrowseAsync(BrowseVolume req, CancellationToken ct)
    {
        var output = new MemoryStream();
        var (code, errors) = await _volumes.HelperAsync(req.Volume, true, ["dotnet", "/app/Dockmaster.Agent.dll", ListDirArg, Inside(req.Path)], null, null, bytes =>
        {
            output.Write(bytes.Span);
            return Task.CompletedTask;
        }, ct);
        if (code != 0) throw new InvalidOperationException(errors.Length > 0 ? errors : $"cannot list {req.Path}");
        return Encoding.UTF8.GetString(output.ToArray());
    }

    /// <summary>
    /// `list-dir /v/path` inside the helper: the folder's entries as JSON on stdout (the agent image has no tool that
    /// prints them in a form worth parsing).
    /// </summary>
    public static int ListDir(string path)
    {
        var dir = new DirectoryInfo(path);
        if (!dir.Exists) { Console.Error.WriteLine($"no folder {path[2..]}"); return 1; }
        var entries = dir.EnumerateFileSystemInfos().Select(i => new Entry(i.Name, i is DirectoryInfo, i is FileInfo f ? f.Length : 0,
            new DateTimeOffset(i.LastWriteTimeUtc).ToUnixTimeSeconds(), i.LinkTarget != null))
            .OrderByDescending(e => e.Dir).ThenBy(e => e.Name, StringComparer.Ordinal).Take(5000).ToList();
        Console.Out.Write(JsonSerializer.Serialize(entries, AgentJson.Web));
        return 0;
    }
}
