using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Dockmaster.Server.Services;

/// <summary>An environment variable of a service as the UI edits it.</summary>
public sealed record EnvVar(string Name, string Value);

/// <summary>A service's image, published ports, environment and restart policy — and a whole new service — as forms write them.</summary>
public static partial class ComposeEditor
{
    public static string? ValidateServiceName(string name) =>
        ServiceNameRegex().IsMatch(name) ? null : "a service name is letters, digits, '_', '.' and '-', e.g. web";

    /// <summary>
    /// An image reference as docker reads it ([registry[:port]/]path[:tag][@digest]), parts of it may be ${VARIABLES}:
    /// written without quotes, so nothing YAML would read as syntax gets through.
    /// </summary>
    public static string? ValidateImage(string image) =>
        ImageRegex().IsMatch(ImageVariableRegex().Replace(image, "x")) ? null : "the image is a name like nginx:1.27 or registry.example.com/app:2";

    /// <summary>"8080:80", "80", "8080:80/udp", "127.0.0.1:8080:80".</summary>
    public static string? ValidatePort(string port) =>
        PortRegex().Match(port) is { Success: true } m && m.Groups.Cast<Group>().Skip(1).Where(g => g.Success && int.TryParse(g.Value, out _)).All(g => int.Parse(g.Value) is > 0 and < 65536)
            ? null : $"'{port}' is not a port: 8080:80 (the server's port : the container's), 80, or 53:53/udp";

    public static string? ValidateEnv(IEnumerable<EnvVar> env)
    {
        var seen = new HashSet<string>();
        foreach (var e in env)
        {
            if (!EnvNameRegex().IsMatch(e.Name)) return $"'{e.Name}' is not a variable name: letters, digits and '_', not starting with a digit";
            if (!seen.Add(e.Name)) return $"{e.Name} is there twice";
            if (e.Value.Contains('\n')) return $"{e.Name}: one line only (put longer text into a project file)";
            if (e.Value.Any(char.IsControl)) return $"{e.Name}: tabs and other control characters cannot be written here (put such text into a project file)";
        }
        return null;
    }

    public static string SetImage(string yaml, string service, string image) => Set(yaml, service, ["image"], image);

    /// <summary>The published ports: a list of short entries (long entries are left to a person).</summary>
    public static string SetPorts(string yaml, string service, IReadOnlyList<string> ports)
    {
        if (Service(yaml, service).Child("ports") is YamlSequenceNode seq && seq.Children.Any(c => c is not YamlScalarNode))
            throw new InvalidOperationException($"the ports of '{service}' are written with options; change them in the compose file");
        return SetSequence(yaml, service, "ports", ports);
    }

    /// <summary>
    /// The service's environment, in the form it already uses: a mapping `KEY: "value"` (also for a service that has
    /// none yet) or a list `- "KEY=value"`. Values are written as typed; `${VAR}` still comes from the .env.
    /// </summary>
    public static string SetEnvironment(string yaml, string service, IReadOnlyList<EnvVar> env)
    {
        var node = Service(yaml, service).Child("environment");
        if (node is YamlSequenceNode)
            return SetSequence(yaml, service, "environment", env.Select(e => $"{e.Name}={e.Value}").ToList());
        if (node is YamlMappingNode m && m.Children.Values.Any(v => v is not YamlScalarNode))
            throw new InvalidOperationException($"the environment of '{service}' is not a list of values; change it in the compose file");
        return SetMapping(yaml, service, "environment", env.Select(e => (e.Name, Quote(e.Value))).ToList());
    }

    /// <summary>
    /// Replaces (or adds at the end of the service) a key whose value is a block mapping of plain values; an empty
    /// list removes the key.
    /// </summary>
    private static string SetMapping(string yaml, string service, string key, IReadOnlyList<(string Key, string Value)> pairs)
    {
        var map = Service(yaml, service);
        var nl = NewLine(yaml);
        string Block(int indent)
        {
            var sb = new StringBuilder().Append(' ', indent).Append(key).Append(':').Append(nl);
            foreach (var (k, v) in pairs) sb.Append(' ', indent + 2).Append(k).Append(": ").Append(v).Append(nl);
            return sb.ToString();
        }
        if (Entry(map, key, $"'{key}' of '{service}'") is not (var keyNode, var node))
        {
            if (pairs.Count == 0) return yaml;
            if (map.Style == MappingStyle.Flow) throw new InvalidOperationException("the compose file uses an inline mapping here; edit the compose file");
            var (pos, prefix, indent) = AppendPoint(yaml, map);
            return yaml.Insert(pos, prefix + Block(indent));
        }
        var end = LineEndAfter(yaml, NodeEnd(yaml, node));
        return yaml[..LineStart(yaml, keyNode)] + (pairs.Count == 0 ? "" : Block((int)keyNode.Start.Column - 1)) + yaml[end..];
    }

    /// <summary>Where a node's text ends; a flow collection ends at its closing bracket (YamlDotNet ends it where it starts).</summary>
    private static int NodeEnd(string yaml, YamlNode node) => node switch
    {
        YamlSequenceNode { Style: SequenceStyle.Flow } s => FlowEnd(yaml, s),
        YamlMappingNode { Style: MappingStyle.Flow } m => yaml.IndexOf('}', m.Children.Count > 0 ? ContentEnd(m) : (int)m.Start.Index) + 1,
        _ => ContentEnd(node),
    };

    /// <summary>
    /// A new service at the end of `services:` — image, restart policy, ports, environment — in the file's indent.
    /// Its data (volumes) is added with <see cref="SetMounts"/> afterwards.
    /// </summary>
    public static string AddService(string yaml, string name, string image, string restart, IReadOnlyList<string> ports, IReadOnlyList<EnvVar> env)
    {
        var root = YamlNodes.LoadRoot(yaml);
        if (root.Child("services") is not YamlMappingNode { Children.Count: > 0 } services || services.Style == MappingStyle.Flow)
            throw new InvalidOperationException("the compose file has no services block to add to; write one service in the Compose tab first");
        if (services.Children.ContainsKey(new YamlScalarNode(name))) throw new InvalidOperationException($"there is a service named {name} already");
        var (pos, prefix, indent) = AppendPoint(yaml, services);
        var nl = NewLine(yaml);
        var pad = new string(' ', indent);
        var sb = new StringBuilder(prefix);
        sb.Append(pad).Append(name).Append(':').Append(nl);
        sb.Append(pad).Append("  image: ").Append(image).Append(nl);
        if (restart.Length > 0) sb.Append(pad).Append("  restart: ").Append(restart == "no" ? "\"no\"" : restart).Append(nl);
        if (ports.Count > 0)
        {
            sb.Append(pad).Append("  ports:").Append(nl);
            foreach (var p in ports) sb.Append(pad).Append("    - ").Append(Quote(p)).Append(nl);
        }
        if (env.Count > 0)
        {
            sb.Append(pad).Append("  environment:").Append(nl);
            foreach (var e in env) sb.Append(pad).Append("    ").Append(e.Name).Append(": ").Append(Quote(e.Value)).Append(nl);
        }
        return yaml.Insert(pos, sb.ToString());
    }

    /// <summary>Takes a service out of the file, with the lines it spans.</summary>
    public static string RemoveService(string yaml, string name)
    {
        if (YamlNodes.LoadRoot(yaml).Child("services") is not YamlMappingNode services || services.Style == MappingStyle.Flow)
            throw new InvalidOperationException("the services are written inline; edit the compose file");
        if (Entry(services, name, $"service '{name}'") is not (var key, var service)) throw new InvalidOperationException($"service '{name}' not found");
        if (services.Children.Count == 1) throw new InvalidOperationException("the last service cannot be removed — delete the project instead");
        return yaml[..LineStart(yaml, key)] + yaml[LineEndAfter(yaml, NodeEnd(yaml, service))..];
    }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,62}$")] private static partial Regex ServiceNameRegex();
    [GeneratedRegex(@"^(?:(?:\d{1,3}\.){3}\d{1,3}:)?(?:(\d{1,5}):)?(\d{1,5})(?:/(?:tcp|udp))?$")] private static partial Regex PortRegex();
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")] private static partial Regex EnvNameRegex();
    [GeneratedRegex(@"^(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]*[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]*[a-zA-Z0-9])?)*(?::[0-9]+)?/)?[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*(?::[A-Za-z0-9_][A-Za-z0-9_.-]{0,127})?(?:@[A-Za-z][A-Za-z0-9]*(?:[-_+.][A-Za-z][A-Za-z0-9]*)*:[0-9a-fA-F]{32,})?$")]
    private static partial Regex ImageRegex();
    [GeneratedRegex(@"\$\{[A-Za-z_][A-Za-z0-9_]*(?::?[-?][A-Za-z0-9_.:/@-]*)?\}")] private static partial Regex ImageVariableRegex();
}
