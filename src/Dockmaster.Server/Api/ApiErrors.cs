using Dockmaster.Protocol;
using Dockmaster.Server.Services;

namespace Dockmaster.Server.Api;

/// <summary>Every failure the UI sees has the shape { error: "…" }.</summary>
public static class ApiErrors
{
    public static IResult Bad(string message) => Results.BadRequest(new { error = message });

    /// <param name="hint">why it may be missing, e.g. "server offline?"</param>
    public static IResult NotFound(string what, string? hint = null) =>
        Results.NotFound(new { error = $"{what} not found" + (hint != null ? $" ({hint})" : "") });

    public static IResult Conflict(string message) => Results.Conflict(new { error = message });

    /// <summary>Signed in, but the user's permissions do not cover it.</summary>
    public static IResult Forbidden(string message) => Results.Json(new { error = message }, statusCode: StatusCodes.Status403Forbidden);

    /// <summary>A registry or other service behind this one failed: 502 with its message.</summary>
    public static IResult Upstream(Exception ex) => Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);

    /// <summary>A command the agent could not carry out; 503 when the agent is not connected at all.</summary>
    public static IResult Agent(CommandResult r) =>
        r.Message == AgentRegistry.OfflineMessage
            ? Results.Json(new { error = r.Message }, statusCode: StatusCodes.Status503ServiceUnavailable)
            : Bad(AgentRegistry.ErrorText(r));

    /// <summary>The agent's JSON answer as it is, or its failure.</summary>
    public static IResult AgentJson(CommandResult r) => r.Ok ? Results.Text(r.Payload, "application/json") : Agent(r);
}
