using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dockmaster.Protocol;

namespace Dockmaster.Agent.Docker;

/// <summary>Docker networks on this host: what is there, and connecting or disconnecting a container live.</summary>
public sealed partial class NetworkStore
{
    private readonly EngineApi _api;

    public NetworkStore(DockerService docker) => _api = docker.Api;

    /// <summary>
    /// docker network connect / disconnect, live. Connecting to a network that does not exist creates it first as a
    /// plain bridge network, the way `docker network create` would.
    /// </summary>
    public async Task<string> ContainerNetworkAsync(string container, string network, bool connect, CancellationToken ct)
    {
        if (!connect)
        {
            await _api.DisconnectNetworkAsync(network, container, ct);
            return $"disconnected from {network}";
        }
        var created = false;
        var exists = (await _api.ListNetworksAsync(ct)).Any(n => n.Name == network || n.ID.StartsWith(network));
        if (!exists)
        {
            await _api.CreateNetworkAsync(network, "bridge", false, ct);
            created = true;
        }
        await _api.ConnectNetworkAsync(network, container, null, ct);
        return created ? $"created network {network} and connected" : $"connected to {network}";
    }

    /// <summary>docker's own networks: never removed from here.</summary>
    private static readonly string[] Builtin = ["bridge", "host", "none", "ingress", "docker_gwbridge"];

    /// <summary>
    /// The host's networks: name, driver, scope, the containers on each (names), its subnet and the compose project
    /// that made it.
    /// </summary>
    public async Task<string> ListJsonAsync(CancellationToken ct)
    {
        var list = await _api.ListNetworksAsync(ct);
        var containers = await _api.ListContainersAsync(true, ct);
        var users = containers.SelectMany(c => (c.NetworkSettings?.Networks?.Keys.ToList() ?? []).Select(n => (n, c.DisplayName())))
            .GroupBy(x => x.n).ToDictionary(g => g.Key, g => g.Select(x => x.Item2).Order().ToArray());
        return JsonSerializer.Serialize(list
            .Where(n => n.Name is not "host" and not "none")
            .OrderBy(n => n.Name)
            .Select(n => new
            {
                name = n.Name, driver = n.Driver, scope = n.Scope, @internal = n.Internal,
                containers = users.GetValueOrDefault(n.Name)?.Length ?? 0, users = users.GetValueOrDefault(n.Name) ?? [],
                subnet = n.IPAM?.Config?.FirstOrDefault()?.Subnet ?? "",
                project = n.Labels != null && n.Labels.TryGetValue(DockerService.ProjectLabel, out var p) ? p : "",
                builtin = Builtin.Contains(n.Name),
                created = DateTimeOffset.TryParse(n.Created, out var at) ? at.ToUnixTimeSeconds() : 0,
            }),
            AgentJson.Web);
    }

    public async Task<string> CreateAsync(CreateNetwork req, CancellationToken ct)
    {
        if (!NameRegex().IsMatch(req.Name)) throw new ArgumentException($"'{req.Name}' is not a network name: letters, digits, '_', '.' and '-'");
        var exists = (await _api.ListNetworksAsync(ct)).Any(n => n.Name == req.Name);
        if (exists) throw new InvalidOperationException($"a network named {req.Name} already exists on this server");
        await _api.CreateNetworkAsync(req.Name, "bridge", req.Internal, ct);
        return $"network {req.Name} created";
    }

    /// <summary>
    /// Removes the given networks (by exact name or full id: docker would also take a prefix of an id or of a name);
    /// one a container is on, and docker's own, are refused.
    /// </summary>
    public async Task<string> RemoveJsonAsync(IEnumerable<string> names, CancellationToken ct)
    {
        var networks = await _api.ListNetworksAsync(ct);
        var containers = await _api.ListContainersAsync(true, ct);
        var removed = new List<string>();
        var failed = new Dictionary<string, string>();
        foreach (var name in names.Distinct())
        {
            if ((networks.FirstOrDefault(n => n.Name == name) ?? networks.FirstOrDefault(n => n.ID == name)) is not { } network) { removed.Add(name); continue; } // gone already
            if (Builtin.Contains(network.Name)) { failed[name] = "docker's own network"; continue; }
            var on = containers.Where(c => c.NetworkSettings?.Networks?.Any(e => e.Key == network.Name || e.Value?.NetworkID == network.ID) == true).Select(c => c.DisplayName()).ToList();
            if (on.Count > 0) { failed[name] = "used by " + string.Join(", ", on); continue; }
            try { await _api.DeleteNetworkAsync(network.ID, ct); removed.Add(name); }
            catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { removed.Add(name); }
            catch (DockerApiException ex) { failed[name] = ex.Message; }
        }
        return JsonSerializer.Serialize(new { removed, failed }, AgentJson.Web);
    }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$")] private static partial Regex NameRegex();
}
