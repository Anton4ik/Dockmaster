using System.Text;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Server.Access;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Server.Api;

/// <summary>/api/projects/{id}/files: a project's files (nginx.conf…), edited on the page and written to the servers on deploy.</summary>
public static class ProjectFilesApi
{
    public static void MapProjectFilesApi(this RouteGroupBuilder g)
    {
        // config files hold passwords as often as the .env does: their contents take Secrets too
        g.MapGet("/{id:int}/files", async (int id, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Projects.FindAsync([id], ct) is not { } p) return ApiErrors.NotFound("project");
            var secrets = await ProjectsApi.SeesSecretsAsync(ctx, p, db, ct);
            return Results.Ok((await db.ProjectFiles.Where(f => f.ProjectId == id).OrderBy(f => f.Name).ToListAsync(ct))
                .Select(f => new ProjectFileDto(f.Name, secrets ? f.Content : "", Mappers.Utc(f.UpdatedAt), Hidden: !secrets, Size: Encoding.UTF8.GetByteCount(f.Content))).ToArray());
        }).ForProject(Perm.View);

        g.MapPut("/{id:int}/files/{name}", async (int id, string name, SaveFileRequest req, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, CancellationToken ct) =>
        {
            name = name.Trim();
            if (ComposeFiles.ValidateName(name) is { } err) return ApiErrors.Bad(err);
            var content = req.Content ?? "";
            if (Encoding.UTF8.GetByteCount(content) > ComposeFiles.MaxSize) return ApiErrors.Bad("a file can be 1 MB at most — a bigger one belongs in a volume or the image");
            await using var db = await dbf.CreateDbContextAsync(ct);
            if (await db.Projects.FindAsync([id], ct) is not { } project) return ApiErrors.NotFound("project");
            // a new file is the user's own; one they were not shown is not theirs to write over
            if (!await ProjectsApi.SeesSecretsAsync(ctx, project, db, ct) && await db.ProjectFiles.AnyAsync(x => x.ProjectId == id && x.Name == name, ct))
                return ApiErrors.Forbidden($"{name} exists, and changing a file you cannot see takes the Secrets permission");
            var others = await db.ProjectFiles.Where(x => x.ProjectId == id && x.Name != name).Select(x => x.Content).ToListAsync(ct);
            if (others.Sum(Encoding.UTF8.GetByteCount) + Encoding.UTF8.GetByteCount(content) > ComposeFiles.MaxTotalSize)
                return ApiErrors.Bad($"the files of a project can be {ComposeFiles.MaxTotalSize >> 20} MB together at most — bigger ones belong in a volume or the image");
            var f = await db.ProjectFiles.FirstOrDefaultAsync(x => x.ProjectId == id && x.Name == name, ct);
            if (f == null)
            {
                if (await db.ProjectFiles.CountAsync(x => x.ProjectId == id, ct) >= ComposeFiles.MaxCount) return ApiErrors.Bad($"a project can have {ComposeFiles.MaxCount} files at most");
                db.ProjectFiles.Add(f = new ProjectFileEntity { ProjectId = id, Name = name });
            }
            var added = f.Id == 0;
            f.Content = content;
            f.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await ctx.AuditAsync($"{(added ? "added" : "saved")} file {name}", id);
            return Results.Ok(new ProjectFileDto(f.Name, f.Content, Mappers.Utc(f.UpdatedAt), Size: Encoding.UTF8.GetByteCount(f.Content)));
        }).ForProject(Perm.Edit);

        g.MapDelete("/{id:int}/files/{name}", async (int id, string name, HttpContext ctx, IDbContextFactory<DockmasterDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            // docker would make an empty folder in the place of a mounted file that is gone, and the service fails
            if (await db.Projects.FindAsync([id], ct) is { } p
                && ComposeServices.Parse(p.ComposeYaml, p.Name).Where(s => ComposeFiles.UsedBy(s).Contains(name)).Select(s => s.Name).ToList() is { Count: > 0 } users)
                return ApiErrors.Conflict($"{string.Join(", ", users)} {(users.Count == 1 ? "uses" : "use")} {name} — stop using it first (Files → Used by, or Services → Data)");
            var n = await db.ProjectFiles.Where(x => x.ProjectId == id && x.Name == name).ExecuteDeleteAsync(ct);
            if (n == 0) return ApiErrors.NotFound("file");
            await ctx.AuditAsync($"deleted file {name}", id);
            return Results.Ok();
        }).ForProject(Perm.Edit);
    }
}
