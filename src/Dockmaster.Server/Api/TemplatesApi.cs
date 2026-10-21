using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Protocol;
using Dockmaster.Server.Access;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Server.Api;

/// <summary>
/// Templates and API keys (signed in), and the instances of a template — for a program with an API key that may manage
/// that template, or for the signed-in admin:
/// <code>
/// GET    /api/templates/{template}/instances
/// POST   /api/templates/{template}/instances             {name, variables, server?, deploy?}
/// GET    /api/templates/{template}/instances/{name}
/// PUT    /api/templates/{template}/instances/{name}      {variables, deploy?}
/// POST   /api/templates/{template}/instances/{name}/{deploy|start|stop|restart}
/// DELETE /api/templates/{template}/instances/{name}[?force=true]
/// </code>
/// The key goes in <c>Authorization: Bearer dmk_…</c>. Operations answer when they are over (<c>?wait=false</c>: 202 at once).
/// </summary>
public static class TemplatesApi
{
    private const int MinHealthTimeoutSeconds = 10;
    private static readonly string[] Ops = ["deploy", "start", "stop", "restart"];

    public static void MapTemplatesApi(this RouteGroupBuilder api)
    {
        MapTemplates(api.MapGroup("/templates").RequireAuthorization().ForAdmin());
        MapApiKeys(api.MapGroup("/apikeys").RequireAuthorization().ForAdmin());
        // the key is checked by each endpoint (AuthorizeAsync)
        MapInstances(api.MapGroup("/templates/{template}/instances").Public());
    }

    private static void MapTemplates(RouteGroupBuilder g)
    {
        g.MapGet("/", async (IDbContextFactory<DockmasterDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var counts = await InstanceCountsAsync(db, ct);
            return (await db.Templates.OrderBy(t => t.Name).ToListAsync(ct)).Select(t => ToDto(t, counts.GetValueOrDefault(t.Id))).ToArray();
        });

        g.MapGet("/{id:int}", async (int id, IDbContextFactory<DockmasterDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Templates.FindAsync([id], ct) is not { } t) return ApiErrors.NotFound("template");
            return Results.Ok(ToDto(t, await db.Projects.CountAsync(p => p.TemplateId == id, ct)));
        });

        g.MapPost("/", async (SaveTemplateRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var t = new TemplateEntity();
            if (await ApplyAsync(t, req, db, ct) is { } bad) return bad;
            if (await db.Templates.AnyAsync(x => x.Name == t.Name, ct)) return ApiErrors.Conflict($"a template named {t.Name} exists already");
            db.Templates.Add(t);
            await db.SaveChangesAsync(ct);
            await ctx.AuditAsync($"added template {t.Name}");
            return Results.Ok(ToDto(t, 0));
        });

        g.MapPut("/{id:int}", async (int id, SaveTemplateRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Templates.FindAsync([id], ct) is not { } t) return ApiErrors.NotFound("template");
            var instances = await db.Projects.CountAsync(p => p.TemplateId == id, ct);
            var oldName = t.Name;
            if (await ApplyAsync(t, req, db, ct) is { } bad) return bad;
            // the instances' project names start with it
            if (t.Name != oldName && instances > 0) return ApiErrors.Conflict($"{oldName} has {instances} instance(s): its name cannot change while they exist");
            if (t.Name != oldName && await db.Templates.AnyAsync(x => x.Name == t.Name && x.Id != id, ct)) return ApiErrors.Conflict($"a template named {t.Name} exists already");
            t.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await ctx.AuditAsync($"saved template {t.Name}" + (t.Name != oldName ? $" (was {oldName})" : ""));
            return Results.Ok(ToDto(t, instances));
        });

        g.MapDelete("/{id:int}", async (int id, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Templates.FindAsync([id], ct) is not { } t) return ApiErrors.NotFound("template");
            var instances = await db.Projects.CountAsync(p => p.TemplateId == id, ct);
            if (instances > 0) return ApiErrors.Conflict($"{t.Name} still has {instances} instance(s): delete them first");
            db.Templates.Remove(t);
            // keys keep working for their other templates
            foreach (var k in await db.ApiKeys.ToListAsync(ct))
                if (k.AllowedTemplateIds.Contains(id)) k.TemplateIds = string.Join(",", k.AllowedTemplateIds.Where(x => x != id));
            await db.SaveChangesAsync(ct);
            await ctx.AuditAsync($"deleted template {t.Name}");
            return Results.Ok();
        });
    }

    private static void MapApiKeys(RouteGroupBuilder g)
    {
        g.MapGet("/", async (IDbContextFactory<DockmasterDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var names = await db.Templates.Select(t => new { t.Id, t.Name }).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
            return (await db.ApiKeys.OrderBy(k => k.Name).ToListAsync(ct)).Select(k => ToDto(k, names)).ToArray();
        });

        g.MapPost("/", async (NewApiKeyRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, CancellationToken ct) =>
        {
            var name = req.Name?.Trim() ?? "";
            if (name.Length is 0 or > 80) return ApiErrors.Bad("give the key a name (who uses it), at most 80 characters");
            await using var db = await dbf.CreateDbContextAsync(ct);
            var names = await db.Templates.Select(t => new { t.Id, t.Name }).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
            var ids = (req.TemplateIds ?? []).Distinct().ToList();
            if (ids.Count == 0) return ApiErrors.Bad("pick the templates the key may manage");
            if (ids.FirstOrDefault(id => !names.ContainsKey(id)) is var missing and > 0) return ApiErrors.Bad($"template #{missing} not found");
            var (key, hash, hint) = Templates.NewKey();
            var k = new ApiKeyEntity { Name = name, KeyHash = hash, Hint = hint, TemplateIds = string.Join(",", ids) };
            db.ApiKeys.Add(k);
            await db.SaveChangesAsync(ct);
            await ctx.AuditAsync($"made API key {name} for " + string.Join(", ", ids.Select(i => names[i])));
            return Results.Ok(new NewApiKeyResponse(ToDto(k, names), key));
        });

        g.MapDelete("/{id:int}", async (int id, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.ApiKeys.FindAsync([id], ct) is not { } k) return ApiErrors.NotFound("API key");
            db.ApiKeys.Remove(k);
            await db.SaveChangesAsync(ct);
            await ctx.AuditAsync($"revoked API key {k.Name}");
            return Results.Ok();
        });
    }

    private static void MapInstances(RouteGroupBuilder g)
    {
        g.MapGet("/", async (string template, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, ProjectService projects, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var (t, _, refused) = await AuthorizeAsync(ctx, template, db, ct);
            if (t == null) return refused!;
            var agents = await db.AgentNamesAsync(ct);
            var list = await db.Projects.Where(p => p.TemplateId == t.Id).OrderBy(p => p.Name).ToListAsync(ct);
            return Results.Ok(list.Select(p => ToDto(p, t, agents, projects)).ToArray());
        });

        g.MapGet("/{name}", async (string template, string name, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, ProjectService projects, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var (t, _, refused) = await AuthorizeAsync(ctx, template, db, ct);
            if (t == null) return refused!;
            if (await FindAsync(db, t, name, ct) is not { } p) return ApiErrors.NotFound("instance");
            return Results.Ok(ToDto(p, t, await db.AgentNamesAsync(ct), projects));
        });

        g.MapPost("/", async (string template, bool? wait, CreateInstanceRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, TemplateService templates,
            ProjectService projects, CancellationToken ct) =>
        {
            TemplateEntity t;
            string source;
            await using (var db = await dbf.CreateDbContextAsync(ct))
            {
                var (found, who, refused) = await AuthorizeAsync(ctx, template, db, ct);
                if (found == null) return refused!;
                (t, source) = (found, who);
            }
            var (p, refusal) = await templates.CreateAsync(t, req.Name ?? "", req.Variables, req.Server, ct);
            if (p == null) return Refuse(refusal!);
            await Audit(ctx, source, $"added instance {Templates.InstanceName(t.Name, p.Name)} of template {t.Name}", p.Id);
            if (req.Deploy == false) return await AnswerAsync(true, "created — deploy or start it to run it", t, p.Id, dbf, projects, ct);
            return await RunAsync(templates.RunAsync(t, p.Id, "deploy", source), wait, t, p.Id, dbf, projects, ct, StatusCodes.Status201Created);
        });

        g.MapPut("/{name}", async (string template, string name, bool? wait, UpdateInstanceRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf,
            TemplateService templates, ProjectService projects, CancellationToken ct) =>
        {
            TemplateEntity t;
            string source;
            int projectId;
            await using (var db = await dbf.CreateDbContextAsync(ct))
            {
                var (found, who, refused) = await AuthorizeAsync(ctx, template, db, ct);
                if (found == null) return refused!;
                if (await FindAsync(db, found, name, ct) is not { } p) return ApiErrors.NotFound("instance");
                (t, source, projectId) = (found, who, p.Id);
            }
            if (await templates.SetVariablesAsync(t, projectId, req.Variables ?? [], ct) is { } refusal) return Refuse(refusal);
            await Audit(ctx, source, $"set the variables of instance {name} of template {t.Name}: {string.Join(", ", (req.Variables ?? []).Keys)}", projectId);
            if (req.Deploy == false) return await AnswerAsync(true, "saved — applied on the next deploy", t, projectId, dbf, projects, ct);
            return await RunAsync(templates.RunAsync(t, projectId, "deploy", source), wait, t, projectId, dbf, projects, ct);
        });

        g.MapPost("/{name}/{op}", async (string template, string name, string op, bool? wait, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf,
            TemplateService templates, ProjectService projects, CancellationToken ct) =>
        {
            op = op.ToLowerInvariant();
            if (!Ops.Contains(op)) return ApiErrors.Bad($"unknown operation '{op}' — one of {string.Join(", ", Ops)}");
            TemplateEntity t;
            string source;
            int projectId;
            await using (var db = await dbf.CreateDbContextAsync(ct))
            {
                var (found, who, refused) = await AuthorizeAsync(ctx, template, db, ct);
                if (found == null) return refused!;
                if (await FindAsync(db, found, name, ct) is not { } p) return ApiErrors.NotFound("instance");
                (t, source, projectId) = (found, who, p.Id);
            }
            return await RunAsync(templates.RunAsync(t, projectId, op, source), wait, t, projectId, dbf, projects, ct);
        });

        g.MapDelete("/{name}", async (string template, string name, bool? force, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, TemplateService templates,
            CancellationToken ct) =>
        {
            int projectId, agentId;
            string source;
            await using (var db = await dbf.CreateDbContextAsync(ct))
            {
                var (t, who, refused) = await AuthorizeAsync(ctx, template, db, ct);
                if (t == null) return refused!;
                if (await FindAsync(db, t, name, ct) is not { } p) return ApiErrors.NotFound("instance");
                (projectId, agentId, source) = (p.Id, p.AgentId, who);
            }
            var (ok, message) = await templates.DeleteAsync(projectId, force == true, source);
            // the project is gone: the event is about its server
            if (ok) await Audit(ctx, source, $"deleted instance {name} of template {template}: {message}", null, agentId);
            return ok ? Results.Ok(new InstanceResult(true, message, null)) : ApiErrors.Conflict(message);
        });
    }

    /// <summary>
    /// The template, when the caller may manage its instances: a signed-in admin, or an API key that lists it.
    /// <paramref name="Source"/> names the caller in the project's history.
    /// </summary>
    private static async Task<(TemplateEntity? Template, string Source, IResult? Refused)> AuthorizeAsync(HttpContext ctx, string template, DockmasterDb db, CancellationToken ct)
    {
        ApiKeyEntity? key = null;
        if (ctx.AccessOrNull() is { } signedIn)
        {
            if (!signedIn.IsAdmin) return (null, "", ApiErrors.Forbidden("only an admin manages instances of templates by hand"));
        }
        else
        {
            var header = ctx.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : "";
            if (!token.StartsWith(Templates.KeyPrefix, StringComparison.Ordinal))
                return (null, "", Results.Json(new { error = "sign in, or send an API key: Authorization: Bearer dmk_…" }, statusCode: StatusCodes.Status401Unauthorized));
            var hash = Templates.HashKey(token);
            key = await db.ApiKeys.FirstOrDefaultAsync(k => k.KeyHash == hash, ct);
            // the stored hash is compared once more in constant time: the index lookup alone says nothing about timing
            if (key == null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(key.KeyHash), Encoding.ASCII.GetBytes(hash)))
                return (null, "", Results.Json(new { error = "unknown API key" }, statusCode: StatusCodes.Status401Unauthorized));
        }
        var t = await db.Templates.FirstOrDefaultAsync(x => x.Name == template, ct);
        if (key != null)
        {
            if (t == null || !key.AllowedTemplateIds.Contains(t.Id))
                return (null, "", Results.Json(new { error = $"this API key may not manage template '{template}'" }, statusCode: StatusCodes.Status403Forbidden));
            if (key.LastUsedAt is not { } used || DateTime.UtcNow - used > TimeSpan.FromMinutes(1))
            {
                key.LastUsedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            return (t, $"API key {key.Name}", null);
        }
        return t == null ? (null, "", ApiErrors.NotFound("template")) : (t, ctx.AccessOrNull()?.Name ?? "", null);
    }

    private static Task<ProjectEntity?> FindAsync(DockmasterDb db, TemplateEntity t, string name, CancellationToken ct)
    {
        var project = Templates.ProjectName(t.Name, name.Trim().ToLowerInvariant());
        return db.Projects.FirstOrDefaultAsync(p => p.TemplateId == t.Id && p.Name == project, ct);
    }

    /// <summary>Waits for the operation (or answers 202 at once with <paramref name="wait"/> false), then the instance as it is now.</summary>
    private static async Task<IResult> RunAsync(Task<(bool ok, string message)> run, bool? wait, TemplateEntity t, int projectId, IDbContextFactory<DockmasterDb> dbf,
        ProjectService projects, CancellationToken ct, int okStatus = StatusCodes.Status200OK)
    {
        if (wait == false)
        {
            _ = run.ContinueWith(r => _ = r.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return await AnswerAsync(true, "started — see the instance's state", t, projectId, dbf, projects, ct, StatusCodes.Status202Accepted);
        }
        var (ok, message) = await run.WaitAsync(ct);
        // a server that is away is worth retrying later; anything else failed for real
        var failed = message == AgentRegistry.OfflineMessage ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError;
        return await AnswerAsync(ok, message, t, projectId, dbf, projects, ct, ok ? okStatus : failed);
    }

    private static async Task<IResult> AnswerAsync(bool ok, string message, TemplateEntity t, int projectId, IDbContextFactory<DockmasterDb> dbf, ProjectService projects,
        CancellationToken ct, int status = StatusCodes.Status200OK)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var p = await db.Projects.FindAsync([projectId], ct);
        var dto = p == null ? null : ToDto(p, t, await db.AgentNamesAsync(ct), projects);
        // a failure carries `error` like every other answer, so the UI and `curl -f` callers see the message
        return ok ? Results.Json(new InstanceResult(true, message, dto), statusCode: status)
            : Results.Json(new { ok, message, instance = dto, error = message }, statusCode: status);
    }

    /// <summary>An audit event naming the caller: the signed-in admin, or the API key (no user is signed in then).</summary>
    private static Task Audit(HttpContext ctx, string source, string message, int? projectId, int? agentId = null) =>
        ctx.RequestServices.GetRequiredService<Notifier>().AuditAsync(source, message, projectId, agentId);

    private static IResult Refuse(TemplateService.Refusal r) => Results.Json(new { error = r.Message }, statusCode: r.Status);

    /// <summary>Checks and copies what the form sent; the first problem as the answer, or null.</summary>
    private static async Task<IResult?> ApplyAsync(TemplateEntity t, SaveTemplateRequest req, DockmasterDb db, CancellationToken ct)
    {
        var name = req.Name?.Trim().ToLowerInvariant() ?? "";
        if (Templates.ValidateTemplateName(name) is { } badName) return ApiErrors.Bad(badName);
        if (string.IsNullOrWhiteSpace(req.ComposeYaml)) return ApiErrors.Bad("compose file is empty");
        if (ComposeServices.Parse(req.ComposeYaml).Count == 0) return ApiErrors.Bad("the compose file has no services (or is not valid YAML)");
        var variables = (req.Variables ?? []).Select(v => v with
        {
            Name = v.Name?.Trim() ?? "",
            Pattern = string.IsNullOrWhiteSpace(v.Pattern) ? null : v.Pattern.Trim(),
            Description = string.IsNullOrWhiteSpace(v.Description) ? null : v.Description.Trim(),
        }).ToList();
        if (Templates.ValidateVariables(variables) is { } badVariables) return ApiErrors.Bad(badVariables);
        var agentIds = (req.AgentIds ?? []).Distinct().ToList();
        var known = await db.Agents.Select(a => a.Id).ToListAsync(ct);
        if (agentIds.FirstOrDefault(id => !known.Contains(id)) is var missing and > 0) return ApiErrors.Bad($"server #{missing} not found");
        t.Name = name;
        t.Description = req.Description?.Trim() ?? "";
        t.ComposeYaml = req.ComposeYaml;
        t.EnvFile = req.EnvFile ?? "";
        t.Variables = Templates.SerializeVariables(variables);
        t.AgentIds = string.Join(",", agentIds);
        t.MaxInstances = Math.Max(0, req.MaxInstances);
        t.HealthTimeoutSeconds = Math.Max(MinHealthTimeoutSeconds, req.HealthTimeoutSeconds);
        return null;
    }

    private static Task<Dictionary<int, int>> InstanceCountsAsync(DockmasterDb db, CancellationToken ct) =>
        db.Projects.Where(p => p.TemplateId != null).GroupBy(p => p.TemplateId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);

    private static TemplateDto ToDto(TemplateEntity t, int instances)
    {
        var variables = Templates.ParseVariables(t.Variables);
        return new(t.Id, t.Name, t.Description, t.ComposeYaml, t.EnvFile, [.. variables], [.. t.AllowedAgentIds], t.MaxInstances, t.HealthTimeoutSeconds, instances,
            Mappers.Utc(t.UpdatedAt), [.. Templates.Warnings(t.ComposeYaml, variables)]);
    }

    private static ApiKeyDto ToDto(ApiKeyEntity k, IReadOnlyDictionary<int, string> templateNames) => new(k.Id, k.Name, k.Hint, [.. k.AllowedTemplateIds],
        [.. k.AllowedTemplateIds.Select(id => templateNames.GetValueOrDefault(id, $"#{id}"))], Mappers.Utc(k.CreatedAt), Mappers.Utc(k.LastUsedAt));

    private static InstanceDto ToDto(ProjectEntity p, TemplateEntity t, IReadOnlyDictionary<int, string> agents, ProjectService projects)
    {
        var cs = projects.ContainersOf(p, [p.AgentId]).Select(x => x.Container).ToList();
        var running = cs.Count(c => c.State == "running");
        var state = projects.IsRunning(p.Id) ? "deploying"
            : cs.Count == 0 ? (p.LastDeployedAt == null ? "new" : "stopped")
            : running == 0 ? "stopped"
            : cs.Any(c => c.Health == "unhealthy" || c.State == "restarting") ? "unhealthy"
            : cs.Any(c => c.State == "running" && c.Health == "starting") ? "starting"
            : running < cs.Count ? "partial"
            : "running";
        return new InstanceDto(Templates.InstanceName(t.Name, p.Name), t.Name, p.Id, agents.GetValueOrDefault(p.AgentId, "?"), state, running, cs.Count,
            Templates.ParseValues(p.InstanceVariables), Mappers.Utc(p.CreatedAt), Mappers.Utc(p.LastDeployedAt), p.LastDeployOk,
            Math.Round(cs.Sum(c => c.CpuPercent), 1), cs.Sum(c => c.MemoryUsage));
    }
}
