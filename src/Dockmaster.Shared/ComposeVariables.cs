namespace Dockmaster.Protocol;

/// <summary>
/// Names a project's secret may not have as a variable of the docker compose call: compose and the docker CLI read
/// them themselves. DOCKER_HOST would send the deploy, with the registry logins the agent made for it, to another
/// daemon; PATH, LD_PRELOAD or a proxy would run or route the CLI elsewhere.
/// </summary>
public static class ComposeVariables
{
    private static readonly string[] Prefixes = ["DOCKER_", "COMPOSE_", "BUILDX_", "BUILDKIT_", "LD_", "SSL_", "XDG_"];
    private static readonly string[] Names = ["PATH", "HOME", "TMPDIR", "SHELL", "USER"];

    public static bool IsReserved(string name) =>
        Prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
        || Names.Any(n => string.Equals(name, n, StringComparison.OrdinalIgnoreCase))
        || name.EndsWith("_PROXY", StringComparison.OrdinalIgnoreCase);
}
