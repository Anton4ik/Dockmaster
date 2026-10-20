using Microsoft.EntityFrameworkCore;
using Dockmaster.Server.Data;

namespace Dockmaster.Server.Services;

public sealed partial class ProjectService
{
    /// <summary>
    /// Puts the compose file and .env of an earlier deploy back and deploys them (with a pull: an image tag of then is
    /// fetched if it is not on the server any more). Secrets stay as they are now. The files the project had go into
    /// the history first (unless the last deploy has them already), so the rollback can be undone the same way.
    /// </summary>
    /// <param name="user">the signed-in user who asked for it, for the history</param>
    public async Task<(bool ok, string message)> RollbackAsync(int projectId, int deploymentId, Action<string> onLine, CancellationToken ct, string user = "")
    {
        // the files are put back and deployed in one turn: no other deploy or autoscaler run comes in between
        using var held = await LockAsync(projectId, false, ct);
        if (held == null) return (false, "a deploy of this project is running — wait for it to end");
        DateTime at;
        await using (var db = await _db.CreateDbContextAsync(ct))
        {
            var dep = await db.Deployments.Where(d => d.Id == deploymentId && d.ProjectId == projectId)
                .Select(d => new { d.ComposeYaml, d.EnvFile, d.StartedAt }).FirstOrDefaultAsync(ct);
            if (dep is not { ComposeYaml.Length: > 0 }) return (false, "this deployment's files were not kept (it is older than the history of files)");
            if (await db.Projects.FindAsync([projectId], ct) is not { } p) return (false, "project not found");
            var last = await db.Deployments.Where(d => d.ProjectId == projectId && d.ComposeYaml != "").OrderByDescending(d => d.Id)
                .Select(d => new { d.ComposeYaml, d.EnvFile }).FirstOrDefaultAsync(ct);
            if (last == null || last.ComposeYaml != p.ComposeYaml || last.EnvFile != p.EnvFile)
                db.Deployments.Add(new DeploymentEntity
                {
                    ProjectId = projectId, Action = "saved", Source = By(user, $"before rollback to #{deploymentId}"), Ok = true, FinishedAt = DateTime.UtcNow,
                    Message = "the compose file and .env as they were before the rollback", ComposeYaml = p.ComposeYaml, EnvFile = p.EnvFile,
                });
            p.ComposeYaml = dep.ComposeYaml;
            p.EnvFile = dep.EnvFile;
            p.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            at = dep.StartedAt;
        }
        onLine($"the compose file and .env of deployment #{deploymentId} ({at.ToLocalTime():yyyy-MM-dd HH:mm}) are back in the project");
        return await DeployHeldAsync(projectId, null, pull: true, forceRecreate: false, onLine, ct, source: By(user, $"rollback to #{deploymentId}"));
    }
}
