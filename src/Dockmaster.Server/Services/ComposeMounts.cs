using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Dockmaster.Server.Services;

/// <summary>One entry of a service's `volumes:` as the UI shows it.</summary>
/// <param name="Type">
/// "volume" (a docker volume: docker keeps the data), "bind" (a folder of the server), "anonymous" (a volume docker
/// names itself: a re-created container gets a new, empty one), or what a long entry says (tmpfs…)
/// </param>
/// <param name="Source">the volume's key in the compose file, or the folder; "" for an anonymous volume</param>
/// <param name="Target">the path inside the container</param>
/// <param name="Editable">false when the entry uses options the UI does not show (it can only be removed)</param>
/// <param name="VolumeName">the name docker gives the volume on the server; null when it is not a named volume or cannot be told</param>
/// <param name="External">the volume is declared external: it exists on the server already, the project does not create it</param>
public sealed record MountSummary(string Type, string Source, string Target, bool ReadOnly, bool Editable, string? VolumeName = null, bool External = false)
{
    /// <summary>Data that lives on (a named volume or a folder), as opposed to an anonymous volume or tmpfs.</summary>
    public bool Keeps => Type is "volume" or "bind";
    /// <summary>The same data can only be reached from the server it is on (not a share mounted from elsewhere).</summary>
    public bool Writable => Keeps && !ReadOnly;
}

/// <summary>
/// A mount as the UI sends it back: an entry kept as it is (<paramref name="Keep"/> = its index in the service's
/// list), or a new or changed one.
/// </summary>
/// <param name="ExternalName">an existing volume of the server, not created by this project: declared as external under the key <paramref name="Source"/></param>
public sealed record MountEdit(int? Keep, string Type, string Source, string Target, bool ReadOnly, string? ExternalName = null)
{
    /// <summary>Compose's short syntax: "data:/var/lib/data", "./conf:/etc/app:ro", "/cache".</summary>
    public string Short => Type == "anonymous" ? Target : $"{Source}:{Target}" + (ReadOnly ? ":ro" : "");
}

/// <summary>Reading the mounts of a compose file's services, and checking the ones the UI sends.</summary>
public static partial class ComposeMounts
{
    /// <summary>The service's `volumes:` entries in order; nothing when it has none or they cannot be read.</summary>
    public static MountSummary[] Parse(YamlMappingNode root, YamlMappingNode service, string? project)
    {
        if (service.Child("volumes") is not YamlSequenceNode seq) return [];
        var declared = Declared(root);
        return seq.Children.Select(item =>
        {
            var m = Read(item) ?? new MountSummary("?", "", "", false, false);
            if (m.Type != "volume") return m;
            // an undeclared key is an error compose reports on deploy; its name would be the project's own
            var (name, external) = declared.GetValueOrDefault(m.Source);
            return m with { VolumeName = name ?? (external ? m.Source : project != null ? $"{project}_{m.Source}" : null), External = external };
        }).ToArray();
    }

    /// <summary>Top-level volumes: key → (name: …, external: …).</summary>
    public static Dictionary<string, (string? Name, bool External)> Declared(YamlMappingNode root)
    {
        var map = new Dictionary<string, (string?, bool)>();
        if (root.Child("volumes") is not YamlMappingNode vols) return map;
        foreach (var (k, v) in vols.Children)
        {
            if (k is not YamlScalarNode { Value: { } key }) continue;
            var m = v as YamlMappingNode;
            var external = m.Scalar("external") is "true" or "True" or "yes" || m.Child("external", "name") != null;
            map[key] = (m.Scalar("name") is { Length: > 0 } n ? n : m.Scalar("external", "name"), external);
        }
        return map;
    }

    /// <summary>The named volumes (their keys) a service mounts.</summary>
    public static IEnumerable<string> VolumeKeys(YamlMappingNode? service) =>
        service.Child("volumes") is YamlSequenceNode seq
            ? seq.Children.Select(Read).Where(m => m is { Type: "volume" }).Select(m => m!.Source)
            : [];

    private static MountSummary? Read(YamlNode item)
    {
        if (item is YamlScalarNode { Value: { } s })
        {
            var parts = s.Split(':');
            if (parts.Length == 1) return new MountSummary("anonymous", "", s, false, s.StartsWith('/'));
            if (parts.Length > 3) return new MountSummary("bind", parts[0], parts[1], false, false);
            var mode = parts.Length == 3 ? parts[2].Split(',') : [];
            var plain = mode.All(o => o is "ro" or "rw");
            return new MountSummary(IsPath(parts[0]) ? "bind" : "volume", parts[0], parts[1], mode.Contains("ro"), plain);
        }
        if (item is not YamlMappingNode m) return null;
        var type = m.Scalar("type") ?? "volume";
        var source = m.Scalar("source") ?? "";
        var readOnly = m.Scalar("read_only") is "true" or "True" or "yes";
        var simple = m.Children.Keys.All(k => k.Text() is "type" or "source" or "target" or "read_only") && type is "volume" or "bind";
        if (type == "volume" && source.Length == 0) type = "anonymous";
        return new MountSummary(type, source, m.Scalar("target") ?? "", readOnly, simple);
    }

    /// <summary>A bind mount's source (a folder of the server) as opposed to a volume's name.</summary>
    public static bool IsPath(string source) => source.Length > 0 && source[0] is '/' or '.' or '~' or '$';

    /// <summary>What is wrong with the mounts the UI sends (<paramref name="current"/>: the service's entries now); null when they can be written.</summary>
    public static string? Validate(IReadOnlyList<MountEdit> mounts, IReadOnlyList<MountSummary> current)
    {
        var targets = new HashSet<string>();
        foreach (var m in mounts)
        {
            if (m.Keep is { } keep)
            {
                if (keep < 0 || keep >= current.Count) return "the service's mounts changed meanwhile — reload the page";
                if (!targets.Add(current[keep].Target.TrimEnd('/'))) return $"two mounts at {current[keep].Target}";
                continue;
            }
            var target = m.Target.Trim();
            if (!target.StartsWith('/') || target == "/" || target.Contains(':') || target.Any(char.IsControl))
                return $"'{target}' is not a path in the container: it starts with / (e.g. /data), without ':'";
            if (!targets.Add(target.TrimEnd('/'))) return $"two mounts at {target}";
            switch (m.Type)
            {
                case "volume":
                    if (!NameRegex().IsMatch(m.Source)) return $"'{m.Source}' is not a volume name: letters, digits, '_', '.' and '-'";
                    if (m.ExternalName != null && !NameRegex().IsMatch(m.ExternalName)) return $"'{m.ExternalName}' is not a volume name";
                    break;
                case "bind":
                    if (!(m.Source.StartsWith('/') || m.Source.StartsWith("./") || m.Source.StartsWith("../")) || m.Source.Contains(':') || m.Source.Any(char.IsControl))
                        return $"'{m.Source}' is not a folder: /srv/app on the server, or ./data in the project's folder";
                    break;
                case "anonymous":
                    break;
                default:
                    return $"unknown mount type '{m.Type}'";
            }
        }
        return null;
    }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")] private static partial Regex NameRegex();
}
