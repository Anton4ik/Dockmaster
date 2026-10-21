using Dockmaster.Protocol;

namespace Dockmaster.Server.Services;

// Jobs: services with a schedule, started by the JobScheduler (or a person) instead of being kept running.
public sealed partial class ProjectService
{
    private static readonly TimeSpan JobStartTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Starts a job's container on the server it is placed on (<c>compose start</c> of that one service: the container
    /// the last deploy created, with the compose file as it was deployed). Not a deploy: no history entry, nothing
    /// re-created — a job that was never deployed cannot start.
    /// </summary>
    public async Task<(bool Ok, string Message)> RunJobAsync(int projectId, string service, CancellationToken ct)
    {
        using var held = await LockAsync(projectId, true, ct);
        var plan = await PlanAsync(projectId, ct);
        var host = plan.Split.Hosts.FirstOrDefault(h => h.Services.Contains(service)) ?? plan.Split.Hosts.FirstOrDefault();
        if (host == null) return (false, "the project is placed on no server");
        if (!plan.OnlineAgents.Contains(host.AgentId)) return (false, $"server {host.AgentName} is offline");
        var cmd = new Command { ProjectAction = new ProjectAction { Project = plan.Project.Name, Op = ProjectOp.ProjectStart } };
        cmd.ProjectAction.Services.Add(service);
        var r = await _agents.SendAsync(host.AgentId, cmd, JobStartTimeout, ct);
        if (r.Ok) return (true, $"started on {host.AgentName}");
        var error = AgentRegistry.ErrorText(r);
        return (false, error.Contains("no container", StringComparison.OrdinalIgnoreCase) || error.Contains("not found", StringComparison.OrdinalIgnoreCase)
            ? error + " — deploy the project first" : error);
    }
}
