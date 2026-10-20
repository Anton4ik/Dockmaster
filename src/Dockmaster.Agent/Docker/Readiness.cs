namespace Dockmaster.Agent.Docker;

/// <summary>When a new container counts as ready, shared by deploys and re-creates.</summary>
internal static class Readiness
{
    /// <summary>Without a healthcheck: running without a restart for this long (compose's update_config.monitor overrides it).</summary>
    public static readonly TimeSpan Monitor = TimeSpan.FromSeconds(10);

    /// <summary>With a healthcheck: healthy, then this long without a crash.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    /// <summary>How long a new container may take to get there.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(180);

    /// <summary>How often the state is checked meanwhile.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);
}
