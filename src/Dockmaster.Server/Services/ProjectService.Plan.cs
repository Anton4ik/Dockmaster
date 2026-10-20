using Microsoft.EntityFrameworkCore;
using Dockmaster.Protocol;
using Dockmaster.Server.Data;

namespace Dockmaster.Server.Services;

/// <summary>A project split by server, with what stops a deploy.</summary>
/// <param name="NameClashes">servers that run a different Dockmaster project of the same name (also among the split's problems)</param>
public sealed record ProjectPlan(ProjectEntity Project, SplitResult Split, IReadOnlyDictionary<int, string> AgentNames, IReadOnlySet<int> OnlineAgents,
    IReadOnlyDictionary<int, string?> MeshIps, IReadOnlyList<string> NameClashes)
{
    public IEnumerable<int> AgentIds => Split.Hosts.Select(h => h.AgentId);
}

public sealed partial class ProjectService
{
    public async Task<ProjectPlan> PlanAsync(int projectId, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await LoadPlanAsync(db, projectId, ct);
    }

    /// <summary>Splits the project by server and checks what would stop a deploy.</summary>
    public async Task<ProjectPlan> PlanAsync(ProjectEntity project, DockmasterDb db, CancellationToken ct)
    {
        var agents = await db.Agents.ToListAsync(ct);
        var byName = agents.ToDictionary(a => a.Name, a => a.Id, StringComparer.OrdinalIgnoreCase);
        var names = agents.ToDictionary(a => a.Id, a => a.Name);
        var meshIps = agents.ToDictionary(a => a.Id, a => a.InMesh ? a.MeshIp : null);
        var online = _agents.All.Select(s => s.AgentId).ToHashSet();
        var split = ComposeSplitter.Split(new SplitInput(project.ComposeYaml, project.AgentId, byName, names, meshIps, online, project.Extras));
        var problems = split.Problems.ToList();
        var containers = split.Hosts.Select(h => h.AgentId).Concat(project.DeployedIds).Distinct()
            .ToDictionary(id => id, id => (IReadOnlyList<ContainerInfo>)(_agents.Get(id)?.Last?.Containers.ToList() ?? []));
        problems.AddRange(ForeignProjectProblems(project, split, containers));
        var services = ComposeServices.Parse(project.ComposeYaml, project.Name);
        var files = await db.ProjectFiles.Where(f => f.ProjectId == project.Id).Select(f => f.Name).ToListAsync(ct);
        split = split with { Warnings = [.. split.Warnings, .. DataWarnings(project, split, services, containers, names), .. ComposeFiles.Missing(services, files)] };
        var clashes = new List<string>();
        if (split.Spanning)
        {
            // the servers must reach each other: a common network, and a way through it
            var mesh = await _mesh.SnapshotAsync(ct);
            var inMesh = split.Hosts.Where(h => meshIps.GetValueOrDefault(h.AgentId) != null).ToList();
            for (var i = 0; i < inMesh.Count; i++)
                for (var j = i + 1; j < inMesh.Count; j++)
                {
                    var (a, b) = (inMesh[i], inMesh[j]);
                    var path = mesh.Plan.Path(a.AgentId, b.AgentId);
                    if (path == null) problems.Add($"servers {a.AgentName} and {b.AgentName} are not in a common network (Network page)");
                    else if (path.Kind == PathKind.None) problems.Add($"servers {a.AgentName} and {b.AgentName} cannot reach each other: no public server in their network to go through (Network page)");
                }

            // compose project names are unique per host: another project of this name on one of these servers would be run over
            var others = await db.Projects.Where(p => p.Name == project.Name && p.Id != project.Id).ToListAsync(ct);
            foreach (var other in others)
            {
                var otherIds = AgentIdsOf(other, byName).Concat(other.DeployedIds).ToHashSet();
                foreach (var h in split.Hosts.Where(h => otherIds.Contains(h.AgentId)))
                    clashes.Add($"server {h.AgentName} already runs a different project named '{project.Name}'");
            }
            problems.AddRange(clashes);
        }
        return new ProjectPlan(project, split with { Problems = problems }, names, online, meshIps, clashes);
    }

    /// <summary>
    /// A server the project was never deployed to, that already runs containers of a compose project with the same
    /// name: they belong to something else (another Dockmaster, Portainer, a hand-made stack), and the deploy there
    /// would remove them as orphans. On the project's own server with nothing placed elsewhere this is the documented
    /// take-over of an imported project, so it is only checked for projects that span servers.
    /// </summary>
    public static IEnumerable<string> ForeignProjectProblems(ProjectEntity project, SplitResult split, IReadOnlyDictionary<int, IReadOnlyList<ContainerInfo>> containersByAgent)
    {
        if (split.Hosts.Count < 2) yield break;
        var deployed = project.DeployedIds.ToHashSet();
        foreach (var h in split.Hosts.Where(h => !deployed.Contains(h.AgentId)))
        {
            var foreign = containersByAgent.GetValueOrDefault(h.AgentId, []).Where(c => c.Project == project.Name).Select(c => c.Name).ToList();
            if (foreign.Count > 0)
                yield return $"server {h.AgentName} already runs containers of another compose project named '{project.Name}' ({string.Join(", ", foreign.Take(3))}{(foreign.Count > 3 ? ", …" : "")}); a deploy would remove them — rename this project or remove them there";
        }
    }

    /// <summary>
    /// What would happen to the files and data a service keeps (named volumes, folders of the server): a volume is
    /// on one server only, so a service spread over servers has a copy on each, one that moves starts empty, and an
    /// unnamed volume is easily lost. Sockets and system files every server has (docker.sock, localtime) do not count.
    /// </summary>
    public static IEnumerable<string> DataWarnings(ProjectEntity project, SplitResult split, IReadOnlyList<ServiceSummary> services,
        IReadOnlyDictionary<int, IReadOnlyList<ContainerInfo>> containersByAgent, IReadOnlyDictionary<int, string> names)
    {
        string Names(IEnumerable<int> ids) => string.Join(", ", ids.Select(id => names.GetValueOrDefault(id, "?")));
        foreach (var s in services)
        {
            foreach (var a in s.Mounts.Where(m => m.Type == "anonymous"))
                yield return $"service {s.Name}: {a.Target} is an unnamed volume — its data is hard to find and is left behind when the service moves; give it a name (Services → Data)";
            // the project's files go to every server with each deploy: nothing stays behind
            var data = s.Mounts.Where(m => m.Keeps && !IsHostSystemPath(m.Source) && !m.Source.StartsWith(ComposeFiles.Folder)).ToList();
            if (data.Count == 0) continue;
            var what = string.Join(", ", data.Select(m => m.Source).Distinct());
            var on = split.Hosts.Where(h => h.Services.Contains(s.Name) && h.Replicas.GetValueOrDefault(s.Name, 1) > 0).Select(h => h.AgentId).ToList();
            if (on.Count > 1)
                yield return $"service {s.Name} keeps its data in {what}: each of {Names(on)} has its own copy — replicas on different servers do not see each other's files (unless it is a network share every server mounts)";
            var ranOn = containersByAgent.Where(kv => kv.Value.Any(c => c.Project == project.Name && c.Service == s.Name)).Select(kv => kv.Key).ToList();
            var left = ranOn.Where(id => !on.Contains(id)).ToList();
            if (on.Count > 0 && left.Count > 0 && !on.Any(ranOn.Contains))
                yield return $"service {s.Name} moves from {Names(left)} to {Names(on)}: its data ({what}) stays on {Names(left)} and it starts empty on {Names(on)} — copy it over first (server page → Volumes → copy to another server)";
            if (s.Autoscale is { Auto: true } auto && auto.AgentNames.Any(n => !on.Any(id => string.Equals(names.GetValueOrDefault(id), n, StringComparison.OrdinalIgnoreCase))))
                yield return $"service {s.Name}: replicas the autoscaler starts on other servers begin with empty data ({what})";
            if (s.Rolling && s.Replicas == 1 && data.Any(m => m.Writable))
                yield return $"service {s.Name}: a rolling update runs the old and the new container side by side on the same data ({what}) — fine for files, but a database must never run twice on its data: turn rolling off for it";
        }
    }

    /// <summary>Paths every Linux server has, mounted for the host's sake rather than as data: docker's socket, the clock, devices.</summary>
    private static bool IsHostSystemPath(string source) =>
        source is "/etc/localtime" or "/etc/timezone" or "/etc/hostname" or "/var/run/docker.sock" or "/run/docker.sock"
        || source.StartsWith("/dev/") || source.StartsWith("/proc/") || source.StartsWith("/sys/") || source == "/";

    private async Task<ProjectPlan> LoadPlanAsync(DockmasterDb db, int projectId, CancellationToken ct)
    {
        var project = await db.Projects.FindAsync([projectId], ct) ?? throw new InvalidOperationException("project not found");
        return await PlanAsync(project, db, ct);
    }
}
