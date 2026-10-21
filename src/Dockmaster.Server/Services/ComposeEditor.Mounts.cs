using System.Text;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Dockmaster.Server.Services;

/// <summary>A service's mounts (`volumes:`) and the top-level volumes they need.</summary>
public static partial class ComposeEditor
{
    /// <summary>
    /// Rewrites a service's `volumes:` list: kept entries stay as they are written (long syntax, options, comments
    /// after them), new and changed ones are added in the short syntax. A named volume the file does not declare yet
    /// is declared at the top (external when it is an existing volume of the server); a declaration only this service
    /// used goes with its last mount. Check the entries with <see cref="ComposeMounts.Validate"/> first.
    /// </summary>
    public static string SetMounts(string yaml, string project, string service, IReadOnlyList<MountEdit> mounts)
    {
        var before = ComposeMounts.VolumeKeys(Service(yaml, service)).ToHashSet();
        yaml = WriteMountList(yaml, service, mounts);

        // declarations: every named volume in use must be known at the top, and one nobody uses any more goes
        var root = YamlNodes.LoadRoot(yaml);
        var declared = ComposeMounts.Declared(root);
        foreach (var m in mounts.Where(m => m.Keep == null && m.Type == "volume").DistinctBy(m => m.Source))
            if (!declared.ContainsKey(m.Source)) yaml = DeclareVolume(yaml, m.Source, m.ExternalName);
        root = YamlNodes.LoadRoot(yaml);
        var used = (root.Child("services") as YamlMappingNode)?.Children.Values.SelectMany(s => ComposeMounts.VolumeKeys(s as YamlMappingNode)).ToHashSet() ?? [];
        foreach (var key in before.Where(k => !used.Contains(k))) yaml = UndeclareVolume(yaml, key);
        return yaml;
    }

    private static string WriteMountList(string yaml, string service, IReadOnlyList<MountEdit> mounts)
    {
        var node = Entry(Service(yaml, service), "volumes", $"'volumes' of '{service}'")?.Value;
        if (node is not (null or YamlSequenceNode) && !node.IsEmptyValue())
            throw new InvalidOperationException($"'volumes' of '{service}' is not a list; edit the compose file");
        var old = node as YamlSequenceNode;
        var items = old?.Children ?? [];
        if (mounts.Any(m => m.Keep is { } k && (k < 0 || k >= items.Count)))
            throw new InvalidOperationException("the service's mounts changed meanwhile — reload the page");
        if (mounts.Count == 0) return SetSequence(yaml, service, "volumes", []);
        // nothing of the old list is kept: a fresh list, in the style the file has
        if (old == null || old.Children.Count == 0 || mounts.All(m => m.Keep == null))
            return SetSequence(yaml, service, "volumes", mounts.Select(m => m.Short).ToList());

        if (old.Style == SequenceStyle.Flow)
        {
            if (items.Any(i => i is not YamlScalarNode))
                throw new InvalidOperationException($"the volumes of '{service}' are written inline; edit the compose file");
            var text = mounts.Select(m => m.Keep is { } k ? yaml[(int)items[k].Start.Index..(int)items[k].End.Index] : Quote(m.Short));
            return yaml[..(int)old.Start.Index] + "[" + string.Join(", ", text) + "]" + yaml[FlowEnd(yaml, old)..];
        }

        // block list: each old entry is its lines up to the next entry (comments after it go along)
        var start = LineStart(yaml, items[0]);
        var end = LineEndAfter(yaml, ContentEnd(old));
        var chunks = items.Select((item, i) => yaml[LineStart(yaml, item)..(i + 1 < items.Count ? LineStart(yaml, items[i + 1]) : end)]).ToList();
        var dash = yaml.IndexOf('-', start) - start;
        var nl = NewLine(yaml);
        var sb = new StringBuilder();
        foreach (var m in mounts)
        {
            if (m.Keep is { } k)
            {
                sb.Append(chunks[k]);
                if (!chunks[k].EndsWith('\n')) sb.Append(nl); // the file's last line
            }
            else sb.Append(' ', dash).Append("- ").Append(Quote(m.Short)).Append(nl);
        }
        return yaml[..start] + sb + yaml[end..];
    }

    /// <summary>`key: {}` (a volume of the project) or `key: {name: …, external: true}` under the top-level volumes.</summary>
    private static string DeclareVolume(string yaml, string key, string? externalName)
    {
        var nl = NewLine(yaml);
        var root = YamlNodes.LoadRoot(yaml);
        string Entry(int indent)
        {
            var pad = new string(' ', indent);
            return externalName == null ? $"{pad}{key}: {{}}{nl}" : $"{pad}{key}:{nl}{pad}  name: {Quote(externalName)}{nl}{pad}  external: true{nl}";
        }
        if (root.Child("volumes") is YamlMappingNode { Style: not MappingStyle.Flow, Children.Count: > 0 } vols)
        {
            var (pos, prefix, indent) = AppendPoint(yaml, vols);
            return yaml.Insert(pos, prefix + Entry(indent));
        }
        var existing = root.Children.Keys.FirstOrDefault(k => k.Equals(new YamlScalarNode("volumes")));
        if (existing != null)
        {
            // `volumes:` / `volumes: {}` with nothing in it: replaced by a block; anything else inline is left to a person
            var value = root.Children[existing];
            if (!(value.IsEmptyValue() || value is YamlMappingNode { Children.Count: 0 }))
                throw new InvalidOperationException("the top-level volumes are written inline; edit the compose file");
            yaml = yaml[..LineStart(yaml, existing)] + yaml[LineEndAfter(yaml, Math.Max((int)existing.End.Index, (int)value.End.Index))..];
        }
        var tail = yaml.Length > 0 && yaml[^1] != '\n' ? nl : "";
        return yaml + tail + "volumes:" + nl + Entry(2);
    }

    /// <summary>Takes a volume's declaration out of the top-level volumes, and the section when it is left empty.</summary>
    private static string UndeclareVolume(string yaml, string key)
    {
        var root = YamlNodes.LoadRoot(yaml);
        if (root.Child("volumes") is not YamlMappingNode { Style: not MappingStyle.Flow } vols) return yaml;
        var k = vols.Children.Keys.FirstOrDefault(x => x.Equals(new YamlScalarNode(key)));
        if (k == null) return yaml;
        if (vols.Children.Count == 1)
        {
            var section = root.Children.Keys.First(x => x.Equals(new YamlScalarNode("volumes")));
            return yaml[..LineStart(yaml, section)] + yaml[LineEndAfter(yaml, ContentEnd(vols))..];
        }
        return yaml[..LineStart(yaml, k)] + yaml[LineEndAfter(yaml, Math.Max((int)k.End.Index, ContentEnd(vols.Children[k])))..];
    }
}
