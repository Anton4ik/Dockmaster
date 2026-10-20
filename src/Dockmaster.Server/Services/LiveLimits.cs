using Dockmaster.Protocol;

namespace Dockmaster.Server.Services;

/// <summary>Applies memory / CPU limits to running containers in place (docker update through the agent).</summary>
public static class LiveLimits
{
    private static readonly TimeSpan UpdateTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Validates for a live change: docker can raise or lower a limit but not remove it.</summary>
    public static string? Validate(string? memory, string? cpus)
    {
        if (memory == null && cpus == null) return "nothing to change";
        if (memory is "" || cpus is "") return "removing a limit needs a redeploy (docker cannot drop it from a running container)";
        return ComposeEditor.Validate(new ComposeEditor.ServiceLimits(null, memory, cpus));
    }

    public static async Task<(bool ok, string message)> ApplyAsync(AgentRegistry agents, int agentId, IEnumerable<string> containerIds, string? memory, string? cpus, CancellationToken ct)
    {
        var update = new ContainerUpdate
        {
            Memory = memory != null ? ComposeEditor.ParseMemory(memory) : 0,
            Cpus = cpus != null ? ComposeEditor.ParseCpus(cpus) : 0,
        };
        var failures = new List<string>();
        var n = 0;
        foreach (var id in containerIds)
        {
            n++;
            var u = update.Clone();
            u.ContainerId = id;
            var r = await agents.SendAsync(agentId, new Command { ContainerUpdate = u }, UpdateTimeout, ct);
            if (!r.Ok) failures.Add(AgentRegistry.ErrorText(r));
        }
        if (n == 0) return (false, "no running containers to update");
        return failures.Count == 0 ? (true, $"applied live to {n} container(s)") : (false, string.Join("; ", failures.Distinct()));
    }
}
