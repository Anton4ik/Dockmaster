using Microsoft.EntityFrameworkCore;
using Dockmaster.Server.Access;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Server.Api;

/// <summary>The mesh network between servers. Join / leave / benchmark stream their output through LiveHub.</summary>
public static class MeshApi
{
    public static void MapMeshApi(this RouteGroupBuilder api)
    {
        // the Network page is the admins'; the mesh as such is also read by the project page (which servers reach each other)
        var g = api.MapGroup("/mesh").RequireAuthorization().ForAdmin();

        g.MapGet("/", async (HttpContext ctx, MeshService mesh, Watchers watchers, CancellationToken ct) =>
        {
            var access = ctx.Access();
            if (!access.IsAdmin) return Narrowed((await mesh.SnapshotAsync(ct)).ToDto(mesh), access);
            watchers.MeshSeen(); // the Network page is open: the agents keep their mesh status fresh meanwhile
            return (await mesh.SnapshotAsync(ct)).ToDto(mesh);
        }).ForAnyone();

        // public flag / endpoint / port of one server
        g.MapPut("/agents/{id:int}", async (int id, MeshAgentRequest req, HttpContext ctx, MeshService mesh, CancellationToken ct) =>
            await Answer(await Audited(ctx, await mesh.ConfigureAsync(id, req.Public, req.Endpoint, req.Port, ct), "changed how the server is reached in the mesh", id), mesh, ct));

        // networks: isolated groups of servers (a server may be in several); adding a server that is not in the mesh
        // yet goes through LiveHub.MeshJoin, which streams the join
        g.MapPost("/networks", async (MeshNetworkRequest req, HttpContext ctx, MeshService mesh, CancellationToken ct) =>
            await Answer(await Audited(ctx, (await mesh.CreateNetworkAsync(req.Name ?? "", ct)).error, $"added mesh network {req.Name}"), mesh, ct));
        g.MapPut("/networks/{id:int}", async (int id, MeshNetworkRequest req, HttpContext ctx, MeshService mesh, CancellationToken ct) =>
            await Answer(await Audited(ctx, await mesh.UpdateNetworkAsync(id, req.Name ?? "", req.HubAgentId, req.Trusted, ct), $"changed mesh network {req.Name}"), mesh, ct));
        g.MapDelete("/networks/{id:int}", async (int id, HttpContext ctx, MeshService mesh, CancellationToken ct) =>
            await Answer(await Audited(ctx, await mesh.DeleteNetworkAsync(id, ct), $"deleted mesh network #{id}"), mesh, ct));
        g.MapDelete("/networks/{id:int}/members/{agentId:int}", async (int id, int agentId, HttpContext ctx, MeshService mesh, CancellationToken ct) =>
            await Answer(await Audited(ctx, await mesh.RemoveFromNetworkAsync(id, agentId, ct), $"took the server out of mesh network #{id}", agentId), mesh, ct));

        // projects that span servers and which service is used from where
        g.MapGet("/connections", async (IDbContextFactory<DockmasterDb> dbf, ProjectService projects, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var list = new List<ConnectionDto>();
            foreach (var p in await db.Projects.OrderBy(p => p.Name).ToListAsync(ct))
            {
                var plan = await projects.PlanAsync(p, db, ct);
                if (plan.Split.Spanning) list.Add(plan.ToConnection());
            }
            return list.ToArray();
        });

        // push the planned config to every server whose config differs (normally happens by itself)
        g.MapPost("/apply", async (HttpContext ctx, MeshService mesh, CancellationToken ct) =>
        {
            var lines = new List<string>();
            await mesh.PushAllAsync(lines.Add, ct);
            await ctx.AuditAsync("pushed the mesh configs to the servers");
            return Results.Ok(new { lines });
        });
    }

    /// <summary>The mesh with only the servers the user sees: their nodes, the paths between them, their networks.</summary>
    private static MeshDto Narrowed(MeshDto m, UserAccess access) => m with
    {
        Nodes = [.. m.Nodes.Where(n => access.Sees(n.AgentId))],
        Pairs = [.. m.Pairs.Where(p => access.Sees(p.From) && access.Sees(p.To))],
        Networks = [.. m.Networks.Where(n => n.Members.Any(access.Sees)).Select(n => n with
        {
            Members = [.. n.Members.Where(access.Sees)],
            HubAgentId = n.HubAgentId is { } hub && access.Sees(hub) ? hub : null,
            HubOverride = n.HubOverride is { } chosen && access.Sees(chosen) ? chosen : null,
        })],
        Problems = [],
        Hints = [],
    };

    /// <summary>A change's refusal, or the whole mesh as it is after the change.</summary>
    /// <summary>The error a change came to; none: what the admin did goes into the events.</summary>
    private static async Task<string?> Audited(HttpContext ctx, string? error, string what, int? agentId = null)
    {
        if (error == null) await ctx.AuditAsync(what, agentId: agentId);
        return error;
    }

    private static async Task<IResult> Answer(string? error, MeshService mesh, CancellationToken ct) =>
        error != null ? ApiErrors.Bad(error) : Results.Ok((await mesh.FreshSnapshotAsync(ct)).ToDto(mesh));
}
