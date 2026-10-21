using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Dockmaster.Server.Services;

/// <summary>What one server of a project gets: its services, as a compose file of their own.</summary>
/// <param name="Replicas">service в†’ how many of its replicas run here (a service spread over servers has a share on each)</param>
/// <param name="Frontends">services other servers run that this server reaches through a balancer of its own</param>
public sealed record HostPlan(int AgentId, string AgentName, IReadOnlyList<string> Services, string ComposeYaml, IReadOnlyList<ExposedService> Exposed,
    IReadOnlyDictionary<string, int>? Replicas = null, IReadOnlyList<Frontend>? Frontends = null)
{
    public IReadOnlyDictionary<string, int> Replicas { get; } = Replicas ?? new Dictionary<string, int>();
    public IReadOnlyList<Frontend> Frontends { get; } = Frontends ?? [];
}

/// <param name="Address">where the other servers reach it: the host's mesh address the ports are published on</param>
/// <param name="Balanced">
/// several replicas (on one server or spread): each is published on a port docker picks and the others reach them
/// through a balancer; otherwise the one replica holds the port itself
/// </param>
public sealed record ExposedService(string Service, IReadOnlyList<string> Ports, string Address, bool Balanced = false);

/// <summary>
/// A balancer on a server for a service spread over servers: the name points at the server's own mesh address, and
/// the connections go to the replicas on every server.
/// </summary>
public sealed record Frontend(string Service, IReadOnlyList<string> Ports, string Address);

/// <param name="Addresses">agent id в†’ the address other servers reach it at (its mesh ip), null = not in the mesh</param>
/// <param name="Extra">replicas the autoscaler added: service в†’ agent id в†’ how many on that server</param>
public sealed record SplitInput(string Yaml, int DefaultAgentId, IReadOnlyDictionary<string, int> AgentIdsByName, IReadOnlyDictionary<int, string> AgentNames,
    IReadOnlyDictionary<int, string?> Addresses, IReadOnlySet<int> OnlineAgents, IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>>? Extra = null);

/// <param name="Problems">what stops a deploy</param>
/// <param name="Warnings">what the person should know, deploy goes ahead</param>
/// <param name="AcrossServers">
/// laid out for several servers although everything runs on one right now: a service the autoscaler may put on
/// other servers is reached through the balancers from the start, so a replica elsewhere changes nothing here
/// </param>
public sealed record SplitResult(IReadOnlyList<HostPlan> Hosts, IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings, bool AcrossServers = false)
{
    public bool Spanning => Hosts.Count > 1 || AcrossServers;
}

/// <summary>
/// One compose project across several servers. `x-dockmaster: {agent: name}` on a service puts it on that server,
/// `x-dockmaster: {agents: [a, b]}` spreads its replicas over those servers (evenly, the first ones get the rest);
/// the rest run on the project's default server. When services end up on more than one server, each server gets
/// a compose file with only its services and their share of replicas. A service with `expose:` is reachable by name
/// from every server: one replica on one server holds its ports on the host's mesh address and the others get an
/// `extra_hosts` entry for it; several replicas are published on ports docker picks, and a server without a replica
/// of its own reaches them through a balancer on its own mesh address. A service spread over servers (or one the
/// autoscaler may put on other servers, `x-dockmaster: {autoscale: {mode: auto}}`) is reached through the balancer
/// also where it has replicas of its own, so every replica on every server gets its share; its address,
/// mesh-ip:port, is also where a reverse proxy on that server reaches it.
/// A project on a single server passes through byte for byte.
/// </summary>
public static partial class ComposeSplitter
{
    public static SplitResult Split(SplitInput input)
    {
        var problems = new List<string>();
        var warnings = new List<string>();
        var defaultName = input.AgentNames.GetValueOrDefault(input.DefaultAgentId, "?");
        YamlMappingNode services;
        try
        {
            if (YamlNodes.LoadRoot(input.Yaml).Child("services") is YamlMappingNode m) services = m;
            else return Single(input, defaultName, problems, warnings); // the agent reports what compose thinks of it
        }
        catch (Exception)
        {
            return Single(input, defaultName, problems, warnings);
        }

        // where every service goes, and how many of its replicas on each server
        var hostsOf = new Dictionary<string, List<int>>();
        var shares = new Dictionary<string, Dictionary<int, int>>();
        // services reached through the balancers everywhere: spread over servers, or the autoscaler may spread them
        var spread = new HashSet<string>();
        var candidates = new HashSet<int>();
        // the replica count each file says, and the services the autoscaler may give more replicas (in mode auto)
        var written = new Dictionary<string, int>();
        var scaling = new HashSet<string>();
        List<int> Resolve(string name, IEnumerable<string> agentNames)
        {
            var ids = new List<int>();
            foreach (var agentName in agentNames)
            {
                var match = input.AgentIdsByName.FirstOrDefault(kv => string.Equals(kv.Key, agentName, StringComparison.OrdinalIgnoreCase));
                if (match.Key == null) problems.Add($"service {name}: server '{agentName}' does not exist");
                else if (!ids.Contains(match.Value)) ids.Add(match.Value);
            }
            return ids;
        }
        foreach (var (k, v) in services.Children)
        {
            var name = k.Text();
            var svc = v as YamlMappingNode;
            var listed = Resolve(name, ComposeServices.AgentNames(svc));
            if (listed.Count == 0) listed.Add(input.DefaultAgentId);
            var total = int.TryParse(svc.Scalar("deploy", "replicas"), out var r) ? Math.Max(0, r) : 1;
            written[name] = total;
            shares[name] = listed.Select((id, i) => (id, n: total / listed.Count + (i < total % listed.Count ? 1 : 0))).ToDictionary(x => x.id, x => x.n);
            if (total > 0 && total < listed.Count)
                warnings.Add($"service {name}: {total} replica(s) over {listed.Count} servers вЂ” {string.Join(", ", listed.Where(id => shares[name][id] == 0).Select(id => input.AgentNames.GetValueOrDefault(id, "?")))} get none");
            if (ComposeServices.Autoscale(svc) is { Auto: true } auto)
            {
                var allowed = Resolve(name, auto.AgentNames);
                if (allowed.Count == 0) allowed = listed;
                if (allowed.Union(listed).Count() > 1)
                {
                    spread.Add(name);
                    candidates.UnionWith(allowed);
                }
                // the autoscaler's replicas, on the servers it may use, never beyond max
                var room = Math.Max(0, auto.Max - total);
                if (room > 0) scaling.Add(name);
                foreach (var (id, n) in input.Extra?.GetValueOrDefault(name) ?? new Dictionary<int, int>())
                {
                    if (!allowed.Contains(id) || n <= 0 || room == 0) continue;
                    var add = Math.Min(n, room);
                    shares[name][id] = shares[name].GetValueOrDefault(id) + add;
                    room -= add;
                }
            }
            // a server without a replica gets nothing of the service; none at all (replicas: 0) stays on the first
            hostsOf[name] = shares[name].Where(x => x.Value > 0).Select(x => x.Key).DefaultIfEmpty(listed[0]).ToList();
            if (hostsOf[name].Count > 1) spread.Add(name);
            if (PortBalancer.Enabled(svc) && PortBalancer.Problem(name, svc!, services) is { } balancer) problems.Add(balancer);
        }
        var withBalancer = services.Children.Values.Any(v => PortBalancer.Enabled(v as YamlMappingNode));
        var hostIds = hostsOf.Values.SelectMany(h => h).Distinct().ToList();
        if (hostIds.Count == 1 && spread.Count == 0)
        {
            var only = hostIds[0];
            // the file as it is, unless the autoscaler runs more replicas of a service than it says or a balancer
            // takes over ports
            var yaml = input.Yaml;
            var scaled = shares.Where(x => x.Value.Values.Sum() != written[x.Key]).ToList();
            if (scaled.Count > 0 || withBalancer)
            {
                var doc = YamlNodes.LoadRoot(input.Yaml);
                foreach (var (name, share) in scaled)
                    if ((doc.Child("services") as YamlMappingNode).Child(name) is YamlMappingNode svc) SetReplicas(svc, share.Values.Sum());
                PortBalancer.Apply(doc);
                yaml = Save(doc);
            }
            return new SplitResult([new HostPlan(only, input.AgentNames.GetValueOrDefault(only, "?"), hostsOf.Keys.ToList(), yaml, [],
                shares.ToDictionary(x => x.Key, x => x.Value.GetValueOrDefault(only)))], problems, warnings);
        }
        // servers the autoscaler may use must be able to take a replica: in the mesh (being offline only rules them out for now)
        foreach (var id in candidates.Except(hostIds).Where(id => input.Addresses.GetValueOrDefault(id) == null))
            problems.Add($"server {input.AgentNames.GetValueOrDefault(id, "?")} is not in the mesh вЂ” the autoscaler may put replicas there (Network page)");

        // exposed services: where their replicas are reached from the other servers
        var exposed = new Dictionary<string, (List<string> Ports, bool Balanced)>();
        foreach (var (k, v) in services.Children)
        {
            var name = k.Text();
            if (v is not YamlMappingNode svc) continue;
            var ports = ExposeList(svc, name, problems);
            if (ports.Count == 0)
            {
                // reached by nobody on another server: nothing to balance, but its replicas elsewhere would only work for themselves
                if (spread.Contains(name) && ComposeServices.Autoscale(svc) is { Auto: true })
                    warnings.Add($"service {name} has no expose: replicas the autoscaler starts on other servers get no connections");
                continue;
            }
            // several replicas, or the autoscaler may start more: then from the start, so a replica more or less does
            // not change the service's ports (a change would re-create it)
            var balanced = spread.Contains(name) || shares[name].Values.Sum() > 1 || scaling.Contains(name);
            if (balanced)
                foreach (var port in ports.Where(p => p.EndsWith("/udp")))
                    problems.Add($"service {name}: UDP port {port} cannot be balanced over several replicas вЂ” run it as 1 replica on one server");
            else if (svc.Flag("x-dockmaster", "rolling") == true)
                problems.Add($"service {name} is reachable from other servers by its port on the host: with one replica a rolling update is not possible, remove x-dockmaster.rolling (or run 2+ replicas)");
            exposed[name] = (ports, balanced);
        }
        string AddressOf(int hostId) => input.Addresses.GetValueOrDefault(hostId) ?? "";

        var hosts = new List<HostPlan>();
        foreach (var hostId in hostIds.OrderBy(id => id == input.DefaultAgentId ? 0 : 1).ThenBy(id => id))
        {
            var hostName = input.AgentNames.GetValueOrDefault(hostId, "?");
            if (input.Addresses.GetValueOrDefault(hostId) == null) problems.Add($"server {hostName} is not in the mesh вЂ” a project across servers needs every server in it (Network page)");
            else if (!input.OnlineAgents.Contains(hostId)) problems.Add($"server {hostName} is offline");

            var local = hostsOf.Where(kv => kv.Value.Contains(hostId)).Select(kv => kv.Key).ToHashSet();
            var address = AddressOf(hostId);
            // services this server reaches elsewhere: one replica by the address it holds, several through a balancer
            // here; a spread service through the balancer also when replicas of it run here
            var remote = exposed.Where(e => !local.Contains(e.Key) || (e.Value.Balanced && spread.Contains(e.Key))).OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
            var frontends = remote.Where(e => e.Value.Balanced).Select(e => new Frontend(e.Key, e.Value.Ports, address)).ToList();
            // what binds a port on this server's mesh address: single replicas held here, and the balancers
            var bound = exposed.Where(e => local.Contains(e.Key) && !e.Value.Balanced).SelectMany(e => e.Value.Ports.Select(p => (Service: e.Key, Port: p)))
                .Concat(frontends.SelectMany(f => f.Ports.Select(p => (f.Service, Port: p))));
            foreach (var dup in bound.GroupBy(x => x.Port).Where(g => g.Count() > 1))
                problems.Add($"server {hostName}: services {string.Join(" and ", dup.Select(x => x.Service))} both need port {dup.Key} on its mesh address");
            // a port a service here publishes on every address of the host takes the mesh address too
            var published = local.SelectMany(name => HostPorts(services.Child(name) as YamlMappingNode, address).Select(port => (Service: name, Port: port))).ToList();
            foreach (var f in frontends)
                foreach (var port in f.Ports.Where(p => !p.EndsWith("/udp")).Select(p => p.Split('/')[0]))
                    foreach (var clash in published.Where(x => x.Port == port))
                        problems.Add($"server {hostName}: the balancer for {f.Service} needs port {port} on {address}, but {clash.Service} publishes port {port} on the host вЂ” change one of them");

            var doc = YamlNodes.LoadRoot(input.Yaml);
            var docServices = (YamlMappingNode)doc.Child("services")!;
            foreach (var key in docServices.Children.Keys.Where(k => !local.Contains(k.Text())).ToList()) docServices.Children.Remove(key);
            foreach (var (k, v) in docServices.Children)
            {
                var name = k.Text();
                if (v is not YamlMappingNode svc) continue;
                var share = shares[name].GetValueOrDefault(hostId);
                if (share != written[name]) SetReplicas(svc, share);
                PruneDependsOn(svc, name, local, hostsOf, input.AgentNames, warnings);
                if (exposed.TryGetValue(name, out var e))
                    foreach (var port in e.Ports) AppendScalar(svc, "ports", e.Balanced ? $"{address}::{port}" : $"{address}:{port}:{port}");
                foreach (var (other, how) in remote.Where(e => e.Key != name))
                    AddExtraHost(svc, other, how.Balanced ? address : AddressOf(hostsOf[other][0]));
            }
            PruneTopLevel(doc, docServices);
            PortBalancer.Apply(doc);
            hosts.Add(new HostPlan(hostId, hostName, local.OrderBy(x => x, StringComparer.Ordinal).ToList(), Save(doc),
                exposed.Where(e => local.Contains(e.Key)).OrderBy(e => e.Key, StringComparer.Ordinal)
                    .Select(e => new ExposedService(e.Key, e.Value.Ports, address, e.Value.Balanced)).ToList(),
                local.ToDictionary(x => x, x => shares[x].GetValueOrDefault(hostId)), frontends));
        }
        return new SplitResult(hosts, problems, warnings, AcrossServers: true);
    }

    /// <summary>
    /// TCP ports a service publishes on the host that also take <paramref name="address"/>: "8080:80",
    /// "0.0.0.0:8080:80", "{address}:8080:80" or {published: 8080}; ranges and ports docker picks do not count.
    /// </summary>
    private static IEnumerable<string> HostPorts(YamlMappingNode? svc, string address)
    {
        foreach (var node in (svc.Child("ports") as YamlSequenceNode)?.Children ?? [])
        {
            string? ip, published, proto;
            if (node is YamlMappingNode m) (ip, published, proto) = (m.Scalar("host_ip"), m.Scalar("published"), m.Scalar("protocol"));
            else if (node is YamlScalarNode { Value: { } v })
            {
                var parts = v.Split('/');
                proto = parts.Length > 1 ? parts[1] : null;
                var at = parts[0].LastIndexOf(':');
                if (at < 0) continue; // "80": docker picks
                var host = parts[0][..at];
                var colon = host.LastIndexOf(':');
                (ip, published) = colon < 0 ? (null, host) : (host[..colon].Trim('[', ']'), host[(colon + 1)..]);
            }
            else continue;
            if (proto is not (null or "" or "tcp") || string.IsNullOrEmpty(published) || !published.All(char.IsDigit)) continue;
            if (ip is null or "" or "0.0.0.0" or "::" || ip == address) yield return published;
        }
    }

    /// <summary>"5432", "53/udp": a single port, which is what can be published across servers.</summary>
    public static bool IsExposePort(string value) => ExposeRegex().IsMatch(value);

    /// <summary>deploy.replicas of this server's share (the file says the total).</summary>
    private static void SetReplicas(YamlMappingNode svc, int replicas)
    {
        if (svc.Child("deploy") is not YamlMappingNode deploy)
        {
            deploy = new YamlMappingNode();
            svc.Children[new YamlScalarNode("deploy")] = deploy;
        }
        deploy.Children[new YamlScalarNode("replicas")] = new YamlScalarNode(replicas.ToString());
    }

    private static SplitResult Single(SplitInput input, string defaultName, List<string> problems, List<string> warnings) =>
        new([new HostPlan(input.DefaultAgentId, defaultName, [], input.Yaml, [])], problems, warnings);

    private static string Save(YamlMappingNode root)
    {
        var ys = new YamlStream(new YamlDocument(root));
        var w = new StringWriter();
        ys.Save(w, assignAnchors: false);
        // the emitter uses the platform's newline and ends the document with "..."; the file is for Linux hosts and people
        var text = w.ToString().Replace("\r\n", "\n");
        if (text.EndsWith("...\n")) text = text[..^4];
        return text;
    }

    /// <summary>"5432", "53/udp"; ranges are refused (they would need one published port each).</summary>
    private static List<string> ExposeList(YamlMappingNode svc, string name, List<string> problems)
    {
        var list = new List<string>();
        foreach (var v in svc.Child("expose").Scalars().Select(v => v.Trim()).Where(v => v.Length > 0))
        {
            if (!IsExposePort(v)) { problems.Add($"service {name}: expose entry '{v}' is not a port (use 5432 or 53/udp; ranges are not supported across servers)"); continue; }
            list.Add(v);
        }
        return list;
    }

    // depends_on entries for services on other servers cannot be waited for: compose would refuse a name it does not know
    private static void PruneDependsOn(YamlMappingNode svc, string name, HashSet<string> local, Dictionary<string, List<int>> hostsOf, IReadOnlyDictionary<int, string> agentNames, List<string> warnings)
    {
        var key = new YamlScalarNode("depends_on");
        if (!svc.Children.TryGetValue(key, out var node)) return;
        var dropped = new List<string>();
        switch (node)
        {
            case YamlSequenceNode seq:
                foreach (var item in seq.Children.ToList())
                {
                    var dep = item.Text();
                    if (local.Contains(dep)) continue;
                    seq.Children.Remove(item);
                    dropped.Add(dep);
                }
                if (seq.Children.Count == 0) svc.Children.Remove(key);
                break;
            case YamlMappingNode map:
                foreach (var k in map.Children.Keys.ToList())
                {
                    var dep = k.Text();
                    if (local.Contains(dep)) continue;
                    map.Children.Remove(k);
                    dropped.Add(dep);
                }
                if (map.Children.Count == 0) svc.Children.Remove(key);
                break;
        }
        foreach (var dep in dropped.Where(hostsOf.ContainsKey))
            warnings.Add($"{name} depends on {dep}, which runs on {string.Join(", ", hostsOf[dep].Select(id => agentNames.GetValueOrDefault(id, "?")))}: start order across servers is not guaranteed, the app must retry its connection");
    }

    private static void AppendScalar(YamlMappingNode svc, string key, string value)
    {
        if (svc.Child(key) is not YamlSequenceNode seq)
        {
            seq = new YamlSequenceNode();
            svc.Children[new YamlScalarNode(key)] = seq;
        }
        seq.Children.Add(new YamlScalarNode(value) { Style = ScalarStyle.DoubleQuoted });
    }

    private static void AddExtraHost(YamlMappingNode svc, string host, string ip)
    {
        if (svc.Child("extra_hosts") is YamlMappingNode map)
        {
            map.Children[new YamlScalarNode(host)] = new YamlScalarNode(ip);
            return;
        }
        AppendScalar(svc, "extra_hosts", $"{host}:{ip}");
    }

    /// <summary>Top-level volumes / networks / secrets / configs nobody on this server uses are dropped (compose would create them).</summary>
    private static void PruneTopLevel(YamlMappingNode doc, YamlMappingNode services)
    {
        Prune(doc, "volumes", services, VolumeRefs);
        Prune(doc, "networks", services, s => SourceRefs(s, "networks"));
        Prune(doc, "secrets", services, s => SourceRefs(s, "secrets"));
        Prune(doc, "configs", services, s => SourceRefs(s, "configs"));
    }

    private static void Prune(YamlMappingNode doc, string section, YamlMappingNode services, Func<YamlMappingNode, IEnumerable<string>> refs)
    {
        if (doc.Child(section) is not YamlMappingNode map) return;
        var used = services.Children.Values.OfType<YamlMappingNode>().SelectMany(refs).ToHashSet();
        foreach (var k in map.Children.Keys.Where(k => !used.Contains(k.Text())).ToList()) map.Children.Remove(k);
        if (map.Children.Count == 0) doc.Children.Remove(new YamlScalarNode(section));
    }

    // "data:/var/lib/postgresql" and {type: volume, source: data}; bind mounts (./x, /x) are not named volumes
    private static IEnumerable<string> VolumeRefs(YamlMappingNode svc)
    {
        if (svc.Child("volumes") is not YamlSequenceNode seq) yield break;
        foreach (var item in seq.Children)
        {
            if (item is YamlScalarNode s && s.Value != null)
            {
                var source = s.Value.Split(':')[0];
                if (source.Length > 0 && !source.StartsWith('/') && !source.StartsWith('.') && !source.StartsWith('~') && !source.StartsWith('$')) yield return source;
            }
            else if (item is YamlMappingNode m && m.Scalar("source") is { } src && m.Scalar("type") is null or "volume") yield return src;
        }
    }

    // networks: [a, b] / networks: {a: {...}} ; secrets/configs: [name] / [{source: name}]
    private static IEnumerable<string> SourceRefs(YamlMappingNode svc, string key)
    {
        switch (svc.Child(key))
        {
            case YamlSequenceNode seq:
                foreach (var item in seq.Children)
                    if (item is YamlScalarNode s && s.Value != null) yield return s.Value;
                    else if (item is YamlMappingNode m && m.Scalar("source") is { } src) yield return src;
                break;
            case YamlMappingNode map:
                foreach (var k in map.Children.Keys) yield return k.Text();
                break;
        }
    }

    [GeneratedRegex(@"^\d{1,5}(/(tcp|udp))?$")] private static partial Regex ExposeRegex();
}
