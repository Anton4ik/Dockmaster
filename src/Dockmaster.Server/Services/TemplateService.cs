using Microsoft.EntityFrameworkCore;
using Dockmaster.Protocol;
using Dockmaster.Server.Data;

namespace Dockmaster.Server.Services;

/// <summary>
/// Instances of templates: each one an ordinary project (its history, metrics and events work as for any other), made
/// from the template's compose file and .env plus the instance's variables. Before every deploy the instance takes the
/// template's files again, so an edited template reaches the instances on their next deploy.
/// </summary>
public sealed class TemplateService(IDbContextFactory<DockmasterDb> dbf, ProjectService projects, AgentRegistry agents, IHostApplicationLifetime life)
{
    // one new instance at a time: its name and the template's limit are checked and the instance saved without another in between
    private readonly SemaphoreSlim _creating = new(1, 1);

    /// <param name="Status">the HTTP status the API answers a refusal with</param>
    public sealed record Refusal(int Status, string Message);

    /// <summary>A new instance, saved but not deployed yet.</summary>
    /// <param name="agentName">the server to run it on; null = the online allowed server with the most memory free</param>
    public async Task<(ProjectEntity? Project, Refusal? Refused)> CreateAsync(TemplateEntity t, string instance, IReadOnlyDictionary<string, string>? variables, string? agentName,
        CancellationToken ct)
    {
        instance = instance.Trim().ToLowerInvariant();
        if (Templates.ValidateInstanceName(instance) is { } badName) return (null, new(400, badName));
        var (values, error) = Templates.Resolve(Templates.ParseVariables(t.Variables), variables);
        if (values == null) return (null, new(400, error!));

        await _creating.WaitAsync(ct);
        try { return await CreateHeldAsync(t, instance, values, agentName, ct); }
        finally { _creating.Release(); }
    }

    private async Task<(ProjectEntity? Project, Refusal? Refused)> CreateHeldAsync(TemplateEntity t, string instance, Dictionary<string, string> values, string? agentName,
        CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var name = Templates.ProjectName(t.Name, instance);
        // instance names are unique across servers: the API addresses an instance by its name alone
        if (await db.Projects.AnyAsync(p => p.Name == name, ct)) return (null, new(409, $"instance {instance} exists already"));
        if (t.MaxInstances > 0 && await db.Projects.CountAsync(p => p.TemplateId == t.Id, ct) >= t.MaxInstances)
            return (null, new(429, $"template {t.Name} runs its maximum of {t.MaxInstances} instances"));

        var allowed = t.AllowedAgentIds;
        var servers = await db.Agents.Select(a => new { a.Id, a.Name }).ToListAsync(ct);
        int agentId;
        if (!string.IsNullOrWhiteSpace(agentName))
        {
            var chosen = servers.FirstOrDefault(a => string.Equals(a.Name, agentName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (chosen == null || allowed.Count > 0 && !allowed.Contains(chosen.Id)) return (null, new(400, $"instances of {t.Name} may not run on server '{agentName}'"));
            agentId = chosen.Id;
        }
        else
        {
            var counts = await db.Projects.Where(p => p.TemplateId == t.Id).GroupBy(p => p.AgentId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
            var candidates = servers.Where(a => allowed.Count == 0 || allowed.Contains(a.Id))
                .Select(a => (a.Id, Session: agents.Get(a.Id))).Where(a => a.Session != null)
                .Select(a => (a.Id, Share(a.Session!.Last?.Host), counts.GetValueOrDefault(a.Id)));
            if (Templates.PickAgent(candidates) is not { } picked) return (null, new(503, $"no server instances of {t.Name} may run on is online"));
            agentId = picked;
        }

        var project = new ProjectEntity
        {
            Name = name, AgentId = agentId, TemplateId = t.Id,
            ComposeYaml = t.ComposeYaml, EnvFile = Templates.RenderEnv(t.EnvFile, t.Name, instance, values),
            InstanceVariables = Templates.SerializeValues(values), HealthTimeoutSeconds = t.HealthTimeoutSeconds,
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        return (project, null);
    }

    /// <summary>New values for an instance's variables (the ones not given keep theirs); deploy it to apply them.</summary>
    public async Task<Refusal?> SetVariablesAsync(TemplateEntity t, int projectId, IReadOnlyDictionary<string, string> variables, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        if (await db.Projects.FindAsync([projectId], ct) is not { } p) return new(404, "instance not found");
        var rules = Templates.ParseVariables(t.Variables);
        // a variable the template no longer has is dropped, not held against the caller
        var merged = Templates.Known(rules, Templates.ParseValues(p.InstanceVariables));
        foreach (var (k, v) in variables) merged[k] = v;
        var (values, error) = Templates.Resolve(rules, merged);
        if (values == null) return new(400, error!);
        p.InstanceVariables = Templates.SerializeValues(values);
        p.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// Runs an operation on an instance: deploy (the template's files again, then pull &amp; deploy), start, stop or
    /// restart. It belongs to the server, not to the request: a caller that stops waiting does not stop it half way.
    /// </summary>
    public Task<(bool ok, string message)> RunAsync(TemplateEntity t, int projectId, string op, string source)
    {
        var stopping = life.ApplicationStopping;
        return op switch
        {
            "deploy" => DeployAsync(t, projectId, source, stopping),
            "start" => StartAsync(t, projectId, source, stopping),
            "stop" => projects.ActionAsync(projectId, ProjectOp.ProjectStop, null, _ => { }, stopping, source),
            "restart" => projects.ActionAsync(projectId, ProjectOp.ProjectRestart, null, _ => { }, stopping, source),
            _ => Task.FromResult((false, $"unknown operation '{op}'")),
        };
    }

    /// <summary>Takes the instance's containers down and forgets it. A server that cannot take them down keeps the instance, unless forced.</summary>
    public async Task<(bool ok, string message)> DeleteAsync(int projectId, bool force, string source)
    {
        var (down, deleted, message) = await projects.TakeDownAndDeleteAsync(projectId, force, life.ApplicationStopping, source);
        if (!deleted) return (false, $"could not take it down ({message}) — the instance is kept; delete with force=true to forget it anyway");
        return (true, down ? "removed" : $"forgotten; its containers may still run ({message})");
    }

    private async Task<(bool ok, string message)> DeployAsync(TemplateEntity t, int projectId, string source, CancellationToken ct)
    {
        if (await SyncAsync(t, projectId, ct) is { } refused) return (false, refused);
        return await projects.DeployAsync(projectId, null, true, false, _ => { }, ct, source);
    }

    /// <summary>Starts the stopped containers; an instance never deployed (or taken down) is deployed instead.</summary>
    private async Task<(bool ok, string message)> StartAsync(TemplateEntity t, int projectId, string source, CancellationToken ct)
    {
        await using (var db = await dbf.CreateDbContextAsync(ct))
        {
            var p = await db.Projects.FindAsync([projectId], ct);
            if (p == null) return (false, "instance not found");
            if (p.LastDeployedAt == null || !p.DeployedIds.Any()) return await DeployAsync(t, projectId, source, ct);
        }
        return await projects.ActionAsync(projectId, ProjectOp.ProjectStart, null, _ => { }, ct, source);
    }

    /// <summary>
    /// The template's compose file and .env (with the instance's variables) into the instance's project. The variables
    /// are checked against the template as it is now: a new default reaches the instance, a value the edited template
    /// no longer accepts stops the deploy. Why it cannot be deployed, or null.
    /// </summary>
    private async Task<string?> SyncAsync(TemplateEntity t, int projectId, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        if (await db.Projects.FindAsync([projectId], ct) is not { } p) return "instance not found";
        var rules = Templates.ParseVariables(t.Variables);
        var (values, error) = Templates.Resolve(rules, Templates.Known(rules, Templates.ParseValues(p.InstanceVariables)));
        if (values == null) return $"{error} — set the instance's variables again";
        var env = Templates.RenderEnv(t.EnvFile, t.Name, Templates.InstanceName(t.Name, p.Name), values);
        var stored = Templates.SerializeValues(values);
        if (p.ComposeYaml == t.ComposeYaml && p.EnvFile == env && p.InstanceVariables == stored && p.HealthTimeoutSeconds == t.HealthTimeoutSeconds) return null;
        p.ComposeYaml = t.ComposeYaml;
        p.EnvFile = env;
        p.InstanceVariables = stored;
        p.HealthTimeoutSeconds = t.HealthTimeoutSeconds;
        p.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return null;
    }

    private static double Share(HostStats? h) => h is { MemoryTotal: > 0 } ? (double)h.MemoryUsed / h.MemoryTotal : 1;
}
