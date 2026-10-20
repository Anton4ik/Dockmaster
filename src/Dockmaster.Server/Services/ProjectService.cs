using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Protocol;
using Dockmaster.Server.Data;

namespace Dockmaster.Server.Services;

/// <summary>Deploy orchestration: splits the project by server, sends each server its part, streams the log, records the deployment.</summary>
public sealed partial class ProjectService
{
    private readonly IDbContextFactory<DockmasterDb> _db;
    private readonly AgentRegistry _agents;
    private readonly RegistryClient _registries;
    private readonly SecretProtector _secrets;
    private readonly ImageUpdateChecker _updates;
    private readonly MeshService _mesh;
    private readonly RunLogs _runLogs;
    private readonly Notifier _notifier;

    public ProjectService(IDbContextFactory<DockmasterDb> db, AgentRegistry agents, RegistryClient registries, SecretProtector secrets, ImageUpdateChecker updates, MeshService mesh,
        RunLogs runLogs, Notifier notifier)
    {
        _notifier = notifier;
        _db = db;
        _agents = agents;
        _registries = registries;
        _secrets = secrets;
        _updates = updates;
        _mesh = mesh;
        _runLogs = runLogs;
    }

    /// <summary>Id of the Dockmaster project of that compose project name on the agent, if any (its default server, or one of its placements).</summary>
    public async Task<int?> FindAsync(int agentId, string composeProject, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(composeProject)) return null;
        await using var db = await _db.CreateDbContextAsync(ct);
        var candidates = await db.Projects.Where(p => p.Name == composeProject).ToListAsync(ct);
        if (candidates.Count == 0) return null;
        if (candidates.FirstOrDefault(p => p.AgentId == agentId) is { } own) return own.Id;
        var byName = await AgentIdsByNameAsync(db, ct);
        return Pick(candidates, agentId, p => AgentIdsOf(p, byName).Concat(p.DeployedIds).ToHashSet());
    }

    /// <summary><see cref="FindAsync"/> for many compose projects at once (the health checks, every minute): one read of the projects.</summary>
    public async Task<Dictionary<(int AgentId, string Project), int?>> FindAllAsync(IEnumerable<(int AgentId, string Project)> keys, CancellationToken ct)
    {
        var wanted = keys.Where(k => k.Project.Length > 0).Distinct().ToList();
        var result = new Dictionary<(int, string), int?>();
        if (wanted.Count == 0) return result;
        var names = wanted.Select(k => k.Project).Distinct().ToList();
        await using var db = await _db.CreateDbContextAsync(ct);
        var candidates = await db.Projects.Where(p => names.Contains(p.Name)).ToListAsync(ct);
        var byName = await AgentIdsByNameAsync(db, ct);
        var placedOn = candidates.ToDictionary(p => p.Id, p => (IReadOnlySet<int>)AgentIdsOf(p, byName).Concat(p.DeployedIds).ToHashSet());
        foreach (var (agentId, project) in wanted)
            result[(agentId, project)] = Pick(candidates.Where(p => p.Name == project), agentId, p => placedOn[p.Id]);
        return result;
    }

    /// <summary>The project of this name the agent is the default server of, else the first one placed (or deployed) on it.</summary>
    private static int? Pick(IEnumerable<ProjectEntity> candidates, int agentId, Func<ProjectEntity, IReadOnlySet<int>> placedOn)
    {
        ProjectEntity? placed = null;
        foreach (var p in candidates)
        {
            if (p.AgentId == agentId) return p.Id;
            if (placed == null && placedOn(p).Contains(agentId)) placed = p;
        }
        return placed?.Id;
    }

    /// <summary>Stored login for the registry an image reference points at (under any of its addresses).</summary>
    public async Task<List<RegistryCredential>> CredentialsForAsync(string imageRef, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return ImageRef.Credentials([imageRef], await db.Registries.ToListAsync(ct), _registries);
    }

    public static string? ValidateSecretName(string name) =>
        !SecretNameRegex().IsMatch(name) ? "secret name must be letters, digits, '.', '-' or '_'"
        // a secret is also a variable of the compose call
        : ComposeVariables.IsReserved(name) ? $"{name} is read by docker compose itself; give the secret another name"
        : null;

    public static string? ValidateName(string name) =>
        ProjectNameRegex().IsMatch(name) ? null : "name must be lowercase letters, digits, '-' or '_' and start with a letter or digit";

    public static async Task<Dictionary<string, int>> AgentIdsByNameAsync(DockmasterDb db, CancellationToken ct) =>
        await db.Agents.Select(a => new { a.Id, a.Name }).ToDictionaryAsync(a => a.Name, a => a.Id, StringComparer.OrdinalIgnoreCase, ct);

    /// <summary>The servers a project's services are placed on: the default server plus every x-dockmaster.agent that names a known server.</summary>
    public static List<int> AgentIdsOf(ProjectEntity p, IReadOnlyDictionary<string, int> agentIdsByName) =>
        AgentIdsOf(p, ComposeServices.Parse(p.ComposeYaml), agentIdsByName);

    /// <param name="services">the project's services, when the caller has parsed them already</param>
    public static List<int> AgentIdsOf(ProjectEntity p, IEnumerable<ServiceSummary> services, IReadOnlyDictionary<string, int> agentIdsByName)
    {
        var ids = new List<int> { p.AgentId };
        foreach (var s in services)
            foreach (var name in s.AgentNames)
                if (agentIdsByName.TryGetValue(name, out var id) && !ids.Contains(id)) ids.Add(id);
        return ids;
    }

    /// <summary>
    /// Containers of the project, from the last heartbeats, on the servers it is placed on (<paramref name="placedOn"/>,
    /// see AgentIdsOf) and on every server it was deployed to.
    /// </summary>
    public List<(int AgentId, ContainerInfo Container)> ContainersOf(ProjectEntity p, IEnumerable<int> placedOn) =>
        placedOn.Concat(p.DeployedIds).Distinct()
            .SelectMany(id => (_agents.Get(id)?.Last?.Containers ?? []).Where(c => c.Project == p.Name).Select(c => (id, c))).ToList();

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$")] private static partial Regex ProjectNameRegex();
    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$")] private static partial Regex SecretNameRegex();
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")] private static partial Regex ServiceNameRegex();
}
