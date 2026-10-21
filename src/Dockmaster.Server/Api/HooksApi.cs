using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Server.Access;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Server.Api;

/// <summary>
/// A project's deploy webhook: CI calls one secret URL after it pushed an image, and the project is pulled and
/// deployed — the way a deploy.sh on the server did it. /api/projects/{id}/hook manages it (signed in),
/// /api/hooks/{token} is the URL itself (no sign-in: the token is the key).
/// </summary>
public static class HooksApi
{
    public const string HookPrefix = "/api/hooks/";

    public static void MapProjectHookApi(this RouteGroupBuilder projects)
    {
        projects.MapGet("/{id:int}/hook", async (int id, IDbContextFactory<DockmasterDb> dbf, SecretProtector protector, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Projects.FindAsync([id], ct) is not { } p) return ApiErrors.NotFound("project");
            return Results.Ok(new ProjectHookDto(p.HookProtected.Length > 0 ? HookPrefix + protector.Unprotect(p.HookProtected) : null));
        }).ForProject(Perm.Secrets);

        // a new URL (an old one stops working)
        projects.MapPost("/{id:int}/hook", async (int id, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, SecretProtector protector, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Projects.FindAsync([id], ct) is not { } p) return ApiErrors.NotFound("project");
            var token = $"{id}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant()}";
            p.HookProtected = protector.Protect(token);
            await db.SaveChangesAsync(ct);
            await ctx.AuditAsync("made a new webhook URL (the old one stopped working)", id);
            return Results.Ok(new ProjectHookDto(HookPrefix + token));
        }).ForProject(Perm.Secrets, Perm.Deploy);

        projects.MapDelete("/{id:int}/hook", async (int id, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Projects.FindAsync([id], ct) is not { } p) return ApiErrors.NotFound("project");
            p.HookProtected = "";
            await db.SaveChangesAsync(ct);
            await ctx.AuditAsync("turned the webhook off", id);
            return Results.Ok(new ProjectHookDto(null));
        }).ForProject(Perm.Secrets);
    }

    /// <summary>
    /// POST /api/hooks/{token}[?services=api,worker][&amp;tag=1.4.2][&amp;pull=false][&amp;wait=true]: pull &amp; deploy the project.
    /// `tag` first writes the new image tag into the compose file (of the one service named, or of the project's only
    /// service). `wait` answers when the deploy is over: 200 when it worked, 500 when not — so `curl -f` fails the CI
    /// step; otherwise 202 at once and the deploy goes on (see the project's history). A call while a webhook deploy of
    /// the project waits for its turn joins that one (429 when it asks for more than that one does).
    /// </summary>
    public static void MapHooksApi(this RouteGroupBuilder api)
    {
        api.MapPost("/hooks/{token}", async (string token, string? services, string? tag, bool? pull, bool? wait, IDbContextFactory<DockmasterDb> dbf,
            SecretProtector protector, ProjectService projects, IHostApplicationLifetime life, ILoggerFactory logs, CancellationToken ct) =>
        {
            var log = logs.CreateLogger("Dockmaster.Hooks");
            var dash = token.IndexOf('-');
            if (dash <= 0 || !int.TryParse(token[..dash], out var id)) return Results.NotFound();
            await using var db = await dbf.CreateDbContextAsync(ct);
            // the same answer for a wrong token and a missing project: nothing to learn by guessing
            if (await db.Projects.FindAsync([id], ct) is not { HookProtected.Length: > 0 } p
                || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(protector.Unprotect(p.HookProtected)), Encoding.UTF8.GetBytes(token)))
                return Results.NotFound();

            var wanted = services?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? [];
            var all = ComposeServices.Parse(p.ComposeYaml).Select(s => s.Name).ToList();
            if (wanted.FirstOrDefault(s => !all.Contains(s)) is { } unknown) return ApiErrors.Bad($"project {p.Name} has no service '{unknown}'");
            if (!string.IsNullOrWhiteSpace(tag))
            {
                var target = wanted.Count == 1 ? wanted[0] : all.Count == 1 ? all[0] : null;
                if (target == null) return ApiErrors.Bad("with tag, name the one service it is for: ?services=api&tag=1.2.3");
                if (!ComposeEditor.TryEdit(p.ComposeYaml, y => ComposeEditor.SetImageTag(y, target, tag.Trim()), out var yaml, out var refused)) return ApiErrors.Bad(refused);
                if (yaml != p.ComposeYaml)
                {
                    p.ComposeYaml = yaml;
                    p.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
            }

            var mine = new Queued(wanted.Count > 0 ? [.. wanted] : null, pull ?? true);
            var queued = Waiting.GetOrAdd(p.Id, mine);
            if (queued != mine)
            {
                // a deploy that has not started yet reads the project when it does: this call's tag goes out with it
                if (!queued.Covers(mine))
                    return Results.Json(new { message = $"another webhook deploy of {p.Name} is queued — call again once it started", error = "queued" }, statusCode: StatusCodes.Status429TooManyRequests);
                log.LogInformation("webhook: {Project} joins the deploy queued already", p.Name);
                if (wait != true) return Results.Accepted(value: new { message = $"a deploy of {p.Name} is queued already — this call goes out with it" });
                return Answer(await queued.Done.Task.WaitAsync(ct));
            }

            log.LogInformation("webhook: deploy {Project} {Services}{Tag}", p.Name, wanted.Count > 0 ? string.Join(",", wanted) : "(all)", tag is { Length: > 0 } ? " tag " + tag : "");
            // the deploy belongs to the server, not to the request: a CI step that gives up waiting does not stop it half way
            var run = projects.DeployAsync(p.Id, mine.Services, mine.Pull, false, _ => { }, life.ApplicationStopping,
                source: "webhook" + (tag is { Length: > 0 } ? $" (tag {tag.Trim()})" : ""), started: () => Waiting.TryRemove(KeyValuePair.Create(p.Id, mine)));
            _ = run.ContinueWith(t =>
            {
                Waiting.TryRemove(KeyValuePair.Create(p.Id, mine));
                if (t.IsCompletedSuccessfully) mine.Done.TrySetResult(t.Result);
                else
                {
                    // also when the caller that waited for it has gone away
                    log.LogWarning(t.Exception, "webhook deploy of {Project} failed", p.Name);
                    mine.Done.TrySetResult((false, t.Exception?.GetBaseException().Message ?? "cancelled"));
                }
            }, TaskScheduler.Default);
            if (wait != true) return Results.Accepted(value: new { message = $"deploying {p.Name} — see its history in Dockmaster" });
            return Answer(await mine.Done.Task.WaitAsync(ct));
        }).Public();
    }

    private static IResult Answer((bool ok, string message) r) =>
        r.ok ? Results.Ok(new { r.ok, r.message }) : Results.Json(new { r.ok, r.message, error = r.message }, statusCode: StatusCodes.Status500InternalServerError);

    // webhook deploys waiting for their turn, by project: CI that calls again meanwhile (a flood, a retry) joins the one waiting
    private static readonly ConcurrentDictionary<int, Queued> Waiting = new();

    /// <param name="services">null = all of them</param>
    private sealed class Queued(List<string>? services, bool pull)
    {
        public List<string>? Services { get; } = services;
        public bool Pull { get; } = pull;
        public TaskCompletionSource<(bool ok, string message)> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>This deploy does what the other one asks for.</summary>
        public bool Covers(Queued other) =>
            (Services == null || other.Services != null && other.Services.All(Services.Contains)) && (Pull || !other.Pull);
    }
}
