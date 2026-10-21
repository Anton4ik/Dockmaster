using Microsoft.EntityFrameworkCore;
using Dockmaster.Server.Access;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Server.Api;

/// <summary>/api/projects/{id}/secrets and /deployments: a project's secrets and its deploy history.</summary>
public static class ProjectSecretsApi
{
    /// <summary>How many of the latest deployments the history shows.</summary>
    private const int HistoryLength = 50;

    public static void MapProjectSecretsApi(this RouteGroupBuilder g)
    {
        // secrets: values are write-only, the API never returns them
        g.MapGet("/{id:int}/secrets", async (int id, IDbContextFactory<DockmasterDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            return (await db.ProjectSecrets.Where(s => s.ProjectId == id).OrderBy(s => s.Name).ToListAsync(ct))
                .Select(s => new ProjectSecretDto(s.Name, Mappers.Utc(s.UpdatedAt))).ToArray();
        }).ForProject(Perm.View);

        // who set or deleted one goes into the events (never the value)
        g.MapPut("/{id:int}/secrets/{name}", async (int id, string name, SetSecretRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, SecretProtector secrets,
            Notifier notifier, CancellationToken ct) =>
        {
            name = name.Trim();
            if (ProjectService.ValidateSecretName(name) is { } err) return ApiErrors.Bad(err);
            if (string.IsNullOrEmpty(req.Value)) return ApiErrors.Bad("value is empty");
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (!await db.Projects.AnyAsync(p => p.Id == id, ct)) return ApiErrors.NotFound("project");
            var s = await db.ProjectSecrets.FirstOrDefaultAsync(x => x.ProjectId == id && x.Name == name, ct);
            var added = s == null;
            if (s == null) db.ProjectSecrets.Add(s = new ProjectSecretEntity { ProjectId = id, Name = name });
            s.ValueProtected = secrets.Protect(req.Value);
            s.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await notifier.AuditAsync(ctx.Access(), $"{(added ? "added" : "changed")} secret {s.Name}", id);
            return Results.Ok(new ProjectSecretDto(s.Name, Mappers.Utc(s.UpdatedAt)));
        }).ForProject(Perm.Secrets);

        g.MapDelete("/{id:int}/secrets/{name}", async (int id, string name, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, Notifier notifier, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var n = await db.ProjectSecrets.Where(x => x.ProjectId == id && x.Name == name).ExecuteDeleteAsync(ct);
            if (n == 0) return ApiErrors.NotFound("secret");
            await notifier.AuditAsync(ctx.Access(), $"deleted secret {name}", id);
            return Results.Ok();
        }).ForProject(Perm.Secrets);

        g.MapGet("/{id:int}/deployments", async (int id, IDbContextFactory<DockmasterDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            // without the log and the files: the list stays small
            var list = await db.Deployments.Where(d => d.ProjectId == id).OrderByDescending(d => d.Id).Take(HistoryLength)
                .Select(d => new DeploymentEntity { Id = d.Id, ProjectId = d.ProjectId, Action = d.Action, StartedAt = d.StartedAt, FinishedAt = d.FinishedAt, Ok = d.Ok,
                    Message = d.Message, Source = d.Source, ComposeYaml = d.ComposeYaml.Length > 0 ? "1" : "" })
                .ToListAsync(ct);
            return list.Select(d => d.ToDto()).ToArray();
        }).ForProject(Perm.View);

        // the compose file and .env a deploy put out, next to the project's now: what changed since
        g.MapGet("/{id:int}/deployments/{depId:int}/files", async (int id, int depId, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var d = await db.Deployments.Where(x => x.Id == depId && x.ProjectId == id).Select(x => new { x.ComposeYaml, x.EnvFile }).FirstOrDefaultAsync(ct);
            if (d is not { ComposeYaml.Length: > 0 }) return ApiErrors.NotFound("the files of this deployment");
            var p = await db.Projects.FindAsync([id], ct);
            // the .env and the environment's values take Secrets on every server the project touches, as on the project page
            if (p != null && !await ProjectsApi.SeesSecretsAsync(ctx, p, db, ct))
                return Results.Ok(new DeploymentFilesDto(ComposeEnv.Redact(d.ComposeYaml), "", ComposeEnv.Redact(p.ComposeYaml), "", true));
            return Results.Ok(new DeploymentFilesDto(d.ComposeYaml, d.EnvFile, p?.ComposeYaml ?? "", p?.EnvFile ?? ""));
        }).ForProject(Perm.View);

        g.MapGet("/{id:int}/deployments/{depId:int}/log", async (int id, int depId, IDbContextFactory<DockmasterDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var log = await db.Deployments.Where(x => x.Id == depId && x.ProjectId == id).Select(x => x.Log).FirstOrDefaultAsync(ct);
            return log == null ? ApiErrors.NotFound("deployment") : Results.Text(log);
        }).ForProject(Perm.View);
    }
}
