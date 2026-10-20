using System.Text.RegularExpressions;
using Dockmaster.Protocol;
using Dockmaster.Server.Data;

namespace Dockmaster.Server.Services;

/// <summary>
/// The balancers each server runs for projects spread over servers: a server reaches a service with several replicas
/// elsewhere (or spread over servers, also its own ones) on its own mesh address, and the balancer there spreads the
/// connections over the replicas — found in the heartbeats, where each one is published on a port docker picked on
/// its host's mesh address.
/// </summary>
public static partial class MeshBalancers
{
    /// <param name="addresses">agent id → mesh address, null = not in the mesh</param>
    /// <param name="containers">agent id → its containers from the last heartbeat</param>
    public static Dictionary<int, IReadOnlyList<MeshFrontend>> Plan(IEnumerable<ProjectEntity> projects, IReadOnlyDictionary<string, int> agentIdsByName,
        IReadOnlyDictionary<int, string> agentNames, IReadOnlyDictionary<int, string?> addresses, IReadOnlySet<int> online,
        IReadOnlyDictionary<int, IReadOnlyList<ContainerInfo>> containers)
    {
        var result = new Dictionary<int, List<MeshFrontend>>();
        foreach (var p in projects)
        {
            // cheap tests first: most projects have no services on several servers, and a balancer only fronts a
            // service that `expose`s ports (splitting a compose file parses and re-emits it)
            if (!p.ComposeYaml.Contains("x-dockmaster") || !p.ComposeYaml.Contains("expose")) continue;
            var split = ComposeSplitter.Split(new SplitInput(p.ComposeYaml, p.AgentId, agentIdsByName, agentNames, addresses, online, p.Extras));
            if (!split.Spanning) continue;
            foreach (var host in split.Hosts)
                foreach (var f in host.Frontends)
                    foreach (var port in f.Ports.Where(x => !x.EndsWith("/udp")))
                    {
                        var target = port.Split('/')[0];
                        var backends = split.Hosts.SelectMany(h => h.Exposed.Where(e => e.Service == f.Service && e.Balanced)
                                .SelectMany(e => Replicas(containers.GetValueOrDefault(h.AgentId) ?? [], p.Name, f.Service, e.Address, target)))
                            .Order(StringComparer.Ordinal).ToList();
                        if (!result.TryGetValue(host.AgentId, out var list)) result[host.AgentId] = list = [];
                        list.Add(new MeshFrontend { Name = Name(p.Name, f.Service, target), Bind = $"{f.Address}:{target}", Backends = { backends } });
                    }
        }
        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<MeshFrontend>)kv.Value);
    }

    /// <summary>"address:hostPort" of each running replica, from the "10.77.0.2:32768->8080/tcp" docker reports.</summary>
    private static IEnumerable<string> Replicas(IReadOnlyList<ContainerInfo> containers, string project, string service, string address, string target)
    {
        foreach (var c in containers.Where(c => c.Project == project && c.Service == service && c.State == "running"))
        {
            var bindings = c.Ports.Select(p => PortRegex().Match(p)).Where(m => m.Success && m.Groups["target"].Value == target && m.Groups["proto"].Value == "tcp").ToList();
            // agents before the address was reported: one binding of that port can only be the one on the mesh address
            var mine = bindings.Where(m => m.Groups["ip"].Value == address).ToList();
            if (mine.Count == 0 && bindings is [var only] && only.Groups["ip"].Value == "") mine = [only];
            foreach (var m in mine) yield return $"{address}:{m.Groups["host"].Value}";
        }
    }

    private static string Name(string project, string service, string port) => NameRegex().Replace($"{project}_{service}_{port}", "_");

    [GeneratedRegex(@"^(?:(?<ip>[0-9.]+):)?(?<host>\d+)->(?<target>\d+)/(?<proto>\w+)$")] private static partial Regex PortRegex();
    [GeneratedRegex(@"[^A-Za-z0-9_.-]")] private static partial Regex NameRegex();
}
