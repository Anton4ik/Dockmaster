using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Dockmaster.Server.Services;

/// <summary>A service's networks in the compose file, in step with a live `docker network connect / disconnect`.</summary>
public static partial class ComposeEditor
{
    /// <summary>
    /// The top-level key of the network docker calls <paramref name="dockerName"/>: an explicit `name:`, the key itself
    /// for an external network, else compose's own `project_key`. Null when the file does not know it.
    /// </summary>
    public static string? NetworkKey(string yaml, string project, string dockerName)
    {
        if (dockerName == $"{project}_default") return "default";
        if (YamlNodes.LoadRoot(yaml).Child("networks") is not YamlMappingNode nets) return null;
        foreach (var (k, v) in nets.Children)
        {
            if (k is not YamlScalarNode { Value: { } key }) continue;
            var m = v as YamlMappingNode;
            var name = m.Scalar("name") is { Length: > 0 } n ? n : null;
            var external = m.Scalar("external") is "true" or "True" or "yes";
            if ((name ?? (external ? key : $"{project}_{key}")) == dockerName) return key;
        }
        return null;
    }

    /// <summary>
    /// Attaches a service to a network or detaches it, in the form the service already uses (a list or a map). A network
    /// the file does not know yet is declared at the top as external. A service without `networks` sits on the
    /// default network, so attaching keeps `default` next to the new one. Detaching the last one is refused: compose
    /// would put the service back on the default network at the next deploy.
    /// </summary>
    public static string SetServiceNetwork(string yaml, string project, string service, string dockerName, bool attach)
    {
        var key = NetworkKey(yaml, project, dockerName);
        if (key == null)
        {
            if (!attach) return yaml; // connected by hand, never in the file: nothing to take out
            (yaml, key) = DeclareExternalNetwork(yaml, dockerName);
        }
        var networks = Entry(Service(yaml, service), "networks", $"'networks' of '{service}'")?.Value;
        var last = new InvalidOperationException($"{dockerName} is the last network of {service}: connect another one first");
        if (networks is YamlMappingNode named)
        {
            var current = named.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value).ToList();
            if (attach) return current.Contains(key) ? yaml : Set(yaml, service, ["networks", key], "{}");
            if (!current.Contains(key)) return yaml;
            if (current.Count == 1) throw last;
            var keyNode = named.Children.Keys.First(k => k.Equals(new YamlScalarNode(key)));
            return yaml[..LineStart(yaml, keyNode)] + yaml[LineEndAfter(yaml, Math.Max((int)keyNode.End.Index, ContentEnd(named.Children[keyNode])))..];
        }
        var list = networks is YamlSequenceNode seq ? seq.Scalars().ToList() : ["default"];
        if (attach)
        {
            if (list.Contains(key)) return yaml;
            list.Add(key);
        }
        else
        {
            if (!list.Remove(key)) return yaml;
            if (list.Count == 0) throw last;
        }
        return SetSequence(yaml, service, "networks", list);
    }

    /// <summary>Adds `key: {name: …, external: true}` under the top-level networks (creating that block when missing).</summary>
    private static (string yaml, string key) DeclareExternalNetwork(string yaml, string dockerName)
    {
        var nl = NewLine(yaml);
        var nets = YamlNodes.LoadRoot(yaml).Child("networks");
        var key = dockerName;
        if (nets is YamlMappingNode m)
        {
            while (m.Children.ContainsKey(new YamlScalarNode(key))) key += "_ext";
            if (m.Style == MappingStyle.Flow || m.Children.Count == 0)
                throw new InvalidOperationException("the top-level networks are written inline; edit the compose file");
            var (pos, prefix, indent) = AppendPoint(yaml, m);
            var pad = new string(' ', indent);
            return (yaml.Insert(pos, $"{prefix}{pad}{key}:{nl}{pad}  name: {Quote(dockerName)}{nl}{pad}  external: true{nl}"), key);
        }
        if (nets != null && !nets.IsEmptyValue())
            throw new InvalidOperationException("the top-level networks are not a mapping; edit the compose file");
        if (nets != null) throw new InvalidOperationException("the compose file has an empty top-level networks key; remove it or declare the network by hand");
        var tail = yaml.Length > 0 && yaml[^1] != '\n' ? nl : "";
        return (yaml + $"{tail}networks:{nl}  {key}:{nl}    name: {Quote(dockerName)}{nl}    external: true{nl}", key);
    }
}
