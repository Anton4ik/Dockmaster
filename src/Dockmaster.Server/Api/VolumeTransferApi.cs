using System.Threading.Channels;
using Google.Protobuf;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Net.Http.Headers;
using Dockmaster.Protocol;
using Dockmaster.Server.Access;
using Dockmaster.Server.Services;

namespace Dockmaster.Server.Api;

/// <summary>
/// /api/agents/{id}/volumes/{name}/…: what is inside a volume — its files, downloaded as a tar.gz (or one file),
/// uploaded from one, or copied to a volume of another server (through this server).
/// </summary>
public static class VolumeTransferApi
{
    private const int UploadChunk = 64 * 1024;
    private static readonly TimeSpan BrowseTimeout = TimeSpan.FromMinutes(1);

    public static void MapVolumeTransferApi(this RouteGroupBuilder agents)
    {
        agents.MapGet("/{id:int}/volumes/{name}/browse", async (int id, string name, string? path, AgentRegistry reg, CancellationToken ct) =>
        {
            if (!VolumeName.IsValid(name)) return ApiErrors.Bad("not a volume name");
            return ApiErrors.AgentJson(await reg.SendAsync(id, new Command { BrowseVolume = new BrowseVolume { Volume = name, Path = path ?? "" } }, BrowseTimeout, ct));
        }).ForServer(Perm.Volumes);

        // the volume (or a folder of it) as a tar.gz, or one file (?file=true) — streamed as the agent reads it
        agents.MapGet("/{id:int}/volumes/{name}/export", async (int id, string name, string? path, bool? file, HttpContext ctx, AgentRegistry reg, CancellationToken ct) =>
        {
            if (!VolumeName.IsValid(name)) { ctx.Response.StatusCode = 400; return; }
            if (reg.Get(id) is not { } session)
            {
                ctx.Response.StatusCode = 503;
                await ctx.Response.WriteAsJsonAsync(new { error = AgentRegistry.OfflineMessage }, ct);
                return;
            }
            var single = file == true;
            var download = single ? Path.GetFileName((path ?? "").TrimEnd('/')) : $"{name}{(string.IsNullOrEmpty(path) ? "" : "-" + path.Trim('/').Replace('/', '-'))}-{DateTime.UtcNow:yyyy-MM-dd}.tar.gz";
            var chunks = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
            var cmd = new Command { Id = Guid.NewGuid().ToString("N"), ExportVolume = new ExportVolume { Volume = name, Path = path ?? "", File = single, Acks = session.DataAcks } };
            // leaving early (the browser went away, a write failed) stops the agent too
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var run = reg.SendAsync(id, cmd, onData: bytes => chunks.Writer.TryWrite(bytes), ct: cts.Token).ContinueWith(t => { chunks.Writer.TryComplete(); return t.Result; }, TaskScheduler.Default);
            try
            {
                var started = false;
                await foreach (var bytes in chunks.Reader.ReadAllAsync(ct))
                {
                    if (!started)
                    {
                        started = true;
                        ctx.Response.ContentType = single ? "application/octet-stream" : "application/gzip";
                        ctx.Response.Headers.ContentDisposition = Attachment(download);
                    }
                    await ctx.Response.Body.WriteAsync(bytes, ct);
                    // passed on: the agent, a window ahead at most, may send the next one
                    if (cmd.ExportVolume.Acks) await reg.AckAsync(id, cmd.Id);
                }
                var r = await run;
                // nothing came: the error is still an answer; after the first bytes it can only cut the download short
                if (!started)
                {
                    if (r.Ok) { ctx.Response.ContentType = "application/octet-stream"; ctx.Response.Headers.ContentDisposition = Attachment(download); return; }
                    ctx.Response.StatusCode = r.Message == AgentRegistry.OfflineMessage ? 503 : 400;
                    await ctx.Response.WriteAsJsonAsync(new { error = AgentRegistry.ErrorText(r) }, ct);
                }
                else if (!r.Ok) ctx.Abort();
            }
            finally { cts.Cancel(); }
        }).ForServer(Perm.Volumes);

        // a tar.gz as the request body, unpacked into the volume (?wipe=true replaces what is in it)
        agents.MapPost("/{id:int}/volumes/{name}/import", async (int id, string name, bool? wipe, HttpContext ctx, AgentRegistry reg, CancellationToken ct) =>
        {
            if (!VolumeName.IsValid(name)) return ApiErrors.Bad("not a volume name");
            if (reg.Get(id) is not { } session) return ApiErrors.Agent(new CommandResult { Ok = false, Message = AgentRegistry.OfflineMessage });
            // reading pauses while the agent catches up: that is not a client too slow to keep
            if (ctx.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } rate) rate.MinDataRate = null;
            var cmd = new Command { Id = Guid.NewGuid().ToString("N"), ImportVolume = new ImportVolume { Volume = name, Wipe = wipe == true } };
            using var importing = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var run = reg.SendAsync(id, cmd, ct: importing.Token);
            var buffer = new byte[UploadChunk];
            var sent = true;
            int n;
            // an import that has answered already failed: the rest of the body is not read
            while (sent && !run.IsCompleted && (n = await ctx.Request.Body.ReadAtLeastAsync(buffer, buffer.Length, false, ct)) > 0)
                sent = await reg.SendDataAsync(session, new DataChunk { CommandId = cmd.Id, Data = ByteString.CopyFrom(buffer, 0, n) }, ct);
            if (sent && !run.IsCompleted) sent = await reg.SendDataAsync(session, new DataChunk { CommandId = cmd.Id, Eof = true }, ct);
            // the bytes could not go (the import is over, or the connection gone): nobody waits for the rest
            if (!sent) importing.Cancel();
            var r = await run;
            if (r.Ok) await ctx.AuditAsync($"uploaded data into volume {name}" + (wipe == true ? ", replacing what was in it" : ""), agentId: id);
            return r.Ok ? Results.Ok(new { message = r.Message }) : ApiErrors.Agent(r);
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute()).ForServer(Perm.Volumes);

        // a copy into a volume of another server (or another volume of this one): packed there, unpacked here, through this server
        agents.MapPost("/{id:int}/volumes/{name}/copy", async (int id, string name, CopyVolumeRequest req, HttpContext ctx, AgentRegistry reg, ILoggerFactory logs, CancellationToken ct) =>
        {
            // the server it goes to is written to: the same permission there
            if (!ctx.Access().Can(req.ToAgentId, Perm.Volumes)) return ApiErrors.Forbidden("you have no volumes permission on the server to copy to");
            var target = req.ToName?.Trim() is { Length: > 0 } t ? t : name;
            if (!VolumeName.IsValid(name) || !VolumeName.IsValid(target)) return ApiErrors.Bad("not a volume name");
            if (req.ToAgentId == id && target == name) return ApiErrors.Bad("that is the same volume");
            if (reg.Get(id) is not { } from) return ApiErrors.Bad("the server to copy from is offline");
            if (reg.Get(req.ToAgentId) is not { } to) return ApiErrors.Bad("the server to copy to is offline");
            // each side stops when the other fails: an import cancelled before its end drops what it unpacked, the
            // volume there stays as it was
            using var importCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var exportCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var import = new Command { Id = Guid.NewGuid().ToString("N"), ImportVolume = new ImportVolume { Volume = target, Wipe = req.Wipe } };
            var importing = reg.SendAsync(req.ToAgentId, import, ct: importCts.Token);
            var export = new Command { Id = Guid.NewGuid().ToString("N"), ExportVolume = new ExportVolume { Volume = name, Acks = from.DataAcks } };
            // the bytes go on in order: one writer, one relay; the source is acked once the target has taken a chunk
            var relay = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            long total = 0;
            var pump = Task.Run(async () =>
            {
                await foreach (var bytes in relay.Reader.ReadAllAsync(importCts.Token))
                {
                    if (!await reg.SendDataAsync(to, new DataChunk { CommandId = import.Id, Data = ByteString.CopyFrom(bytes) }, importCts.Token))
                    {
                        // the import is over (it failed) or its connection is gone: nothing more to read or write
                        exportCts.Cancel();
                        importCts.Cancel();
                        return;
                    }
                    total += bytes.Length;
                    if (export.ExportVolume.Acks) await reg.AckAsync(id, export.Id);
                }
            }, importCts.Token);
            var exported = await reg.SendAsync(id, export, onData: bytes => relay.Writer.TryWrite(bytes), ct: exportCts.Token);
            relay.Writer.TryComplete();
            if (!exported.Ok) importCts.Cancel();
            try { await pump; }
            catch (OperationCanceledException) when (importCts.IsCancellationRequested) { }
            if (exported.Ok && !await reg.SendDataAsync(to, new DataChunk { CommandId = import.Id, Eof = true }, importCts.Token)) importCts.Cancel();
            var imported = await importing;
            logs.CreateLogger("Dockmaster.Volumes").LogInformation("copy {Volume} from {From} to {Target} on {To}: {Bytes} bytes, {Result}", name, id, target, req.ToAgentId, total,
                exported.Ok && imported.Ok ? "ok" : !imported.Ok && imported.Message != AgentRegistry.CancelledMessage ? imported.Message : exported.Message);
            // the side that failed first: the other one was only cancelled because of it
            if (!imported.Ok && imported.Message != AgentRegistry.CancelledMessage) return ApiErrors.Bad("writing it failed: " + AgentRegistry.ErrorText(imported));
            if (!exported.Ok) return ApiErrors.Bad($"reading it failed, nothing was changed on {to.Name}: {AgentRegistry.ErrorText(exported)}");
            if (!imported.Ok) return ApiErrors.Bad("writing it failed: " + AgentRegistry.ErrorText(imported));
            // about the server written to
            await ctx.AuditAsync($"copied volume {name} of {from.Name} into {target}" + (req.Wipe ? ", replacing what was in it" : ""), agentId: req.ToAgentId);
            return Results.Ok(new { message = $"copied {Human(total)} (packed) into {target} on {to.Name}" });
        }).ForServer(Perm.Volumes);
    }

    /// <summary>"attachment" with the file name, also one that is not ASCII (a header value must be).</summary>
    private static string Attachment(string fileName)
    {
        var value = new ContentDispositionHeaderValue("attachment");
        value.SetHttpFileName(fileName);
        return value.ToString();
    }

    private static string Human(long b) => b >= 1 << 30 ? $"{b / (double)(1 << 30):0.#} GB" : b >= 1 << 20 ? $"{b / (double)(1 << 20):0.#} MB" : $"{b / 1024.0:0.#} KB";
}
