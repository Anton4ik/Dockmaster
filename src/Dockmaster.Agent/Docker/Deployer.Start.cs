using Dockmaster.Protocol;

namespace Dockmaster.Agent.Docker;

// `compose start` of some services (a job's run) straight through the Engine API.
public sealed partial class Deployer
{
    private const string OneoffLabel = "com.docker.compose.oneoff";

    /// <summary>
    /// Starts the containers of these services with one Engine API call each, where that does what `compose start`
    /// would: a docker CLI child process costs about a second of CPU and puts its 50 MB binary in the agent's page
    /// cache, on every run of every job. Null when compose has to do it (see <see cref="DirectStart"/>).
    /// </summary>
    private async Task<(bool ok, string message)?> StartDirectAsync(ProjectAction req, Func<string, Task> log, CancellationToken ct)
    {
        if (req.Op != ProjectOp.ProjectStart || req.Services.Count == 0 || _compose.ComposeYaml(req.Project) is not { } yaml) return null;
        var project = await _docker.Api.ListContainersAsync(true, ct, new Dictionary<string, string[]> { ["label"] = [$"{DockerService.ProjectLabel}={req.Project}"] });
        if (DirectStart(project, req.Services, s => ComposeSpec.Dependencies(yaml, s)) is not { } start) return null;
        foreach (var (id, name) in start)
        {
            try { await log(await _docker.StartAsync(id, ct) ? $" Container {name}  Started" : $" Container {name}  Running"); }
            catch (DockerApiException ex)
            {
                await log($" Container {name}  Error: {ex.Message}");
                return (false, $"start of {name} failed: {ex.Message}");
            }
        }
        return (true, "start ok");
    }

    /// <summary>
    /// The containers to start for these services (id, name), or null when only compose can say or do it right: a
    /// service has no container (never deployed — compose says so), its dependencies are not known for sure, or a
    /// service it depends on is not where its condition wants it (compose starts that first and waits for it). The
    /// containers' own depends_on label does not tell: a job is created with --no-deps, which leaves it empty.
    /// </summary>
    /// <param name="dependencies">a service's dependencies and their conditions (<see cref="ComposeSpec.Dependencies"/>)</param>
    public static List<(string Id, string Name)>? DirectStart(IReadOnlyList<ContainerListResponse> project, IEnumerable<string> services,
        Func<string, IReadOnlyDictionary<string, string>?> dependencies)
    {
        var mine = project.Where(c => c.Labels?.GetValueOrDefault(OneoffLabel) != "True").ToList();
        static string Service(ContainerListResponse c) => c.Labels?.GetValueOrDefault(DockerService.ServiceLabel) ?? "";
        var result = new List<(string, string)>();
        foreach (var service in services.Distinct())
        {
            var containers = mine.Where(c => Service(c) == service).ToList();
            if (containers.Count == 0 || dependencies(service) is not { } deps) return null;
            foreach (var (dep, condition) in deps)
            {
                var those = mine.Where(d => Service(d) == dep).ToList();
                if (those.Count == 0 || !those.All(d => Satisfies(d, condition))) return null;
            }
            result.AddRange(containers.Select(c => (c.ID, c.Names is [var n, ..] ? n.TrimStart('/') : c.ID[..Math.Min(12, c.ID.Length)])));
        }
        return result;
    }

    // the list's Status: "Up 3 hours (healthy)", "Exited (0) 2 minutes ago"
    private static bool Satisfies(ContainerListResponse c, string condition) => condition switch
    {
        "service_started" => c.State == "running",
        "service_healthy" => c.State == "running" && c.Status.Contains("(healthy)"),
        "service_completed_successfully" => c.State == "exited" && c.Status.StartsWith("Exited (0)"),
        _ => false,
    };
}
