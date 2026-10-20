namespace Dockmaster.Server.Access;

/// <summary>
/// What a user may do on a server. A role is a set of these; a user gets a role per server (or on all of them). Seeing
/// the server comes with any of them.
/// </summary>
public static class Perm
{
    /// <summary>The server, its containers, projects, logs, metrics, events and deploy history.</summary>
    public const string View = "view";
    /// <summary>Start, stop, restart, kill and remove containers; start / stop / down projects; run a job now.</summary>
    public const string Control = "control";
    /// <summary>Deploy projects, pull &amp; re-create containers, roll back to an earlier deploy.</summary>
    public const string Deploy = "deploy";
    /// <summary>Create, import, change and delete projects: compose file, services, files, limits, placement.</summary>
    public const string Edit = "edit";
    /// <summary>Project secrets, the .env file, the deploy webhook and the environment of containers.</summary>
    public const string Secrets = "secrets";
    /// <summary>A shell in containers.</summary>
    public const string Terminal = "terminal";
    /// <summary>Volumes and what is in them: browse, download, upload, copy, create, remove.</summary>
    public const string Volumes = "volumes";
    /// <summary>The host itself: images, docker networks, updating the agent, renaming the server.</summary>
    public const string Server = "server";

    public sealed record Info(string Key, string Label, string Description);

    /// <summary>Every permission, in the order the UI lists them.</summary>
    public static readonly IReadOnlyList<Info> All =
    [
        new(View, "View", "the server, its containers, projects, logs, metrics, events and deploy history"),
        new(Control, "Start & stop", "start, stop, restart and remove containers; start, stop and take down projects; run a job now"),
        new(Deploy, "Deploy", "deploy projects, pull & re-create containers, roll back to an earlier deploy"),
        new(Edit, "Edit projects", "create, import, change and delete projects: compose file, services, files, limits, placement"),
        new(Secrets, "Secrets", "project secrets, the .env file, the project's files, the deploy webhook and the environment of containers and services"),
        new(Terminal, "Terminal", "a shell in containers"),
        new(Volumes, "Volumes", "browse, download, upload, copy, create and remove volumes"),
        new(Server, "Server", "images, docker networks, updating the agent, renaming the server"),
    ];

    public static readonly IReadOnlySet<string> Keys = All.Select(p => p.Key).ToHashSet();

    /// <summary>The known keys of a comma separated list, in the canonical order; View is always among them.</summary>
    public static IReadOnlyList<string> Parse(string csv) => Normalize(csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public static IReadOnlyList<string> Normalize(IEnumerable<string> keys)
    {
        var set = keys.Where(Keys.Contains).ToHashSet();
        set.Add(View);
        return All.Select(p => p.Key).Where(set.Contains).ToList();
    }
}

/// <summary>The roles a new installation starts with.</summary>
public static class DefaultRoles
{
    /// <summary>Marks the defaults as created: a role the admin deleted stays deleted.</summary>
    public const string SeededMarkerKey = "roles.seeded_v1";

    public sealed record Default(string Name, string Description, string[] Permissions);

    public static readonly IReadOnlyList<Default> All =
    [
        new("Viewer", "Sees the servers and projects, changes nothing", [Perm.View]),
        new("Operator", "Starts, stops and deploys what is there", [Perm.View, Perm.Control, Perm.Deploy]),
        new("Developer", "Builds and runs projects: everything but the host itself",
            [Perm.View, Perm.Control, Perm.Deploy, Perm.Edit, Perm.Secrets, Perm.Terminal, Perm.Volumes]),
        new("Server admin", "Everything on the server", [.. Perm.All.Select(p => p.Key)]),
    ];
}
