using Dockmaster.Server.Api;
using Dockmaster.Server.Services;

namespace Dockmaster.Server.Access;

public enum AccessKind
{
    /// <summary>No sign-in needed (signing in itself, webhooks, API-key calls that check their key).</summary>
    Public,
    /// <summary>Anyone signed in; what they get back is narrowed to what they may see.</summary>
    SignedIn,
    /// <summary>Admins only.</summary>
    Admin,
    /// <summary>The permissions on the server named by the route's {id}.</summary>
    Server,
    /// <summary>The permissions on the servers of the project named by the route's {id}.</summary>
    Project,
}

/// <summary>Who may call an endpoint. Every /api endpoint has one: an endpoint without is for admins only.</summary>
public sealed record AccessRule(AccessKind Kind, IReadOnlyList<string> Perms);

/// <summary>The access rules of the endpoints, and the filter on /api that enforces them.</summary>
public static class AccessRules
{
    private const string ItemKey = "dockmaster.access";

    public static TBuilder Public<TBuilder>(this TBuilder b) where TBuilder : IEndpointConventionBuilder => b.WithMetadata(new AccessRule(AccessKind.Public, []));

    public static TBuilder ForAnyone<TBuilder>(this TBuilder b) where TBuilder : IEndpointConventionBuilder => b.WithMetadata(new AccessRule(AccessKind.SignedIn, []));

    public static TBuilder ForAdmin<TBuilder>(this TBuilder b) where TBuilder : IEndpointConventionBuilder => b.WithMetadata(new AccessRule(AccessKind.Admin, []));

    /// <summary>All of <paramref name="perms"/> on the server {id}.</summary>
    public static TBuilder ForServer<TBuilder>(this TBuilder b, params string[] perms) where TBuilder : IEndpointConventionBuilder =>
        b.WithMetadata(new AccessRule(AccessKind.Server, perms));

    /// <summary>All of <paramref name="perms"/> on the project {id}'s servers (View: on its own server).</summary>
    public static TBuilder ForProject<TBuilder>(this TBuilder b, params string[] perms) where TBuilder : IEndpointConventionBuilder =>
        b.WithMetadata(new AccessRule(AccessKind.Project, perms));

    /// <summary>The caller's access, as the filter found it (endpoints that are not public only).</summary>
    public static UserAccess Access(this HttpContext ctx) =>
        ctx.Items[ItemKey] as UserAccess ?? throw new InvalidOperationException("no access rule ran for this endpoint");

    /// <summary>What the signed-in caller did, as an audit event (<see cref="Notifier.AuditAsync(UserAccess, string, int?, int?, string?)"/>).</summary>
    public static Task AuditAsync(this HttpContext ctx, string message, int? projectId = null, int? agentId = null, string? container = null) =>
        ctx.RequestServices.GetRequiredService<Notifier>().AuditAsync(ctx.Access(), message, projectId, agentId, container);

    /// <summary>The caller's access when signed in, also on a public endpoint.</summary>
    public static UserAccess? AccessOrNull(this HttpContext ctx) => ctx.Items[ItemKey] as UserAccess;

    /// <summary>The endpoint filter of /api: checks the endpoint's <see cref="AccessRule"/> before it runs.</summary>
    public static async ValueTask<object?> Enforce(EndpointFilterInvocationContext ic, EndpointFilterDelegate next)
    {
        var http = ic.HttpContext;
        var rule = http.GetEndpoint()?.Metadata.GetMetadata<AccessRule>();
        var service = http.RequestServices.GetRequiredService<AccessService>();
        var access = await service.ForAsync(http.User, http.RequestAborted);
        if (access != null) http.Items[ItemKey] = access;
        if (rule?.Kind == AccessKind.Public) return await next(ic);
        if (access == null) return Results.Json(new { error = "not signed in" }, statusCode: StatusCodes.Status401Unauthorized);

        switch (rule?.Kind)
        {
            case AccessKind.SignedIn:
                break;
            case AccessKind.Server:
                if (!RouteId(http, out var agentId)) return ApiErrors.Bad("no server in the address");
                // a server that is not the user's is not there for them
                if (!access.Sees(agentId)) return ApiErrors.NotFound("server");
                if (rule.Perms.FirstOrDefault(p => !access.Can(agentId, p)) is { } missing)
                    return ApiErrors.Forbidden($"you have no {AccessService.Label(missing)} permission on this server");
                break;
            case AccessKind.Project:
                if (!RouteId(http, out var projectId)) return ApiErrors.Bad("no project in the address");
                var (found, denied) = await service.ProjectDeniedAsync(access, projectId, rule.Perms, http.RequestAborted);
                if (!found) return ApiErrors.NotFound("project");
                if (denied != null) return ApiErrors.Forbidden(denied);
                break;
            default: // Admin, and an endpoint nobody gave a rule
                if (!access.IsAdmin) return ApiErrors.Forbidden("only an admin may do this");
                break;
        }
        return await next(ic);
    }

    private static bool RouteId(HttpContext http, out int id)
    {
        id = 0;
        return http.Request.RouteValues.TryGetValue("id", out var v) && int.TryParse(v?.ToString(), out id);
    }
}
