using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;
using ScalarEvent = YamlDotNet.Core.Events.Scalar;

namespace Dockmaster.Server.Services;

/// <summary>Reading helpers over YamlDotNet's node model, shared by everything that looks into a compose file.</summary>
internal static class YamlNodes
{
    /// <summary>The most aliases (*name) a file may hold.</summary>
    public const int MaxAliases = 1000;
    /// <summary>The most nodes aliases may add to a file once they are expanded (what writing it out or comparing keys walks).</summary>
    public const int MaxAliasNodes = 100_000;

    /// <summary>
    /// The top-level mapping of the first document. Refuses (<see cref="YamlException"/>) a file whose aliases expand
    /// to more than <see cref="MaxAliasNodes"/> nodes, and a mapping key that is not a plain value.
    /// </summary>
    public static YamlMappingNode LoadRoot(string yaml)
    {
        CheckShape(yaml);
        var ys = new YamlStream();
        ys.Load(new StringReader(yaml));
        return ys.Documents.FirstOrDefault()?.RootNode as YamlMappingNode ?? throw new InvalidOperationException("compose file has no top-level mapping");
    }

    /// <summary>
    /// The node model keeps one node per anchor, but writing a node out, its ToString and the hash of a mapping key walk
    /// every alias again: nested aliases ("billion laughs") turn a 1 KB file into gigabytes. Counted on the parser's events
    /// before anything is built.
    /// </summary>
    private static void CheckShape(string yaml)
    {
        var parser = new Parser(new StringReader(yaml));
        var anchors = new Dictionary<string, (long Size, bool Scalar)>();
        // per open mapping or sequence: its node count so far, its anchor, and (mappings) whether a key comes next
        var open = new Stack<(long Size, string? Anchor, bool Mapping, bool KeyNext)>();
        long extra = 0;
        var aliases = 0;

        void Item(long size, bool container, bool scalar, Mark at)
        {
            if (open.Count == 0) return;
            var top = open.Pop();
            if (top.Mapping && top.KeyNext && (container || !scalar))
                throw new YamlException(at, at, "a mapping key must be a plain value");
            open.Push(top with { Size = top.Size + size, KeyNext = top.Mapping && !top.KeyNext });
        }

        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case ScalarEvent s:
                    Item(1, false, true, s.Start);
                    if (!s.Anchor.IsEmpty) anchors[s.Anchor.Value] = (1, true);
                    break;
                case AnchorAlias a:
                    if (++aliases > MaxAliases) throw new YamlException(a.Start, a.End, $"more than {MaxAliases} aliases");
                    if (!anchors.TryGetValue(a.Value.Value, out var target)) break;  // the loader reports an unknown alias
                    extra += target.Size;
                    if (extra > MaxAliasNodes) throw new YamlException(a.Start, a.End, $"the aliases expand to more than {MaxAliasNodes} nodes");
                    Item(target.Size, false, target.Scalar, a.Start);
                    break;
                case MappingStart m:
                    Item(0, true, false, m.Start);
                    open.Push((1, m.Anchor.IsEmpty ? null : m.Anchor.Value, true, true));
                    break;
                case SequenceStart q:
                    Item(0, true, false, q.Start);
                    open.Push((1, q.Anchor.IsEmpty ? null : q.Anchor.Value, false, false));
                    break;
                case MappingEnd or SequenceEnd:
                    var done = open.Pop();
                    if (done.Anchor != null) anchors[done.Anchor] = (done.Size, false);
                    if (open.Count > 0)
                    {
                        // the size was not known when the item started: add it now, the key/value turn already moved on
                        var parent = open.Pop();
                        open.Push(parent with { Size = parent.Size + done.Size });
                    }
                    break;
            }
        }
    }

    /// <summary>The node under a path of keys; null when a key is missing or a step is not a mapping.</summary>
    public static YamlNode? Child(this YamlMappingNode? node, params string[] path)
    {
        YamlNode? cur = node;
        foreach (var key in path)
        {
            if (cur is YamlMappingNode m && m.Children.TryGetValue(new YamlScalarNode(key), out var next)) cur = next;
            else return null;
        }
        return cur;
    }

    /// <summary>The plain value under a path of keys; null when it is missing or not a scalar.</summary>
    public static string? Scalar(this YamlMappingNode? node, params string[] path) => (node.Child(path) as YamlScalarNode)?.Value;

    /// <summary>A true/false value under a path of keys (any case, like the agent reads it); null when missing or not a boolean.</summary>
    public static bool? Flag(this YamlMappingNode? node, params string[] path) => bool.TryParse(node.Scalar(path), out var b) ? b : null;

    /// <summary>A key with nothing after it, or with ~ / null.</summary>
    public static bool IsEmptyValue(this YamlNode? node) => node is YamlScalarNode { Value: null or "" or "~" or "null" };

    /// <summary>The text of a mapping key or list item; "" when it is not a plain value (never its expanded ToString).</summary>
    public static string Text(this YamlNode? node) => (node as YamlScalarNode)?.Value ?? "";

    /// <summary>The scalar items of a sequence (other items skipped); nothing when the node is not a sequence.</summary>
    public static IEnumerable<string> Scalars(this YamlNode? node) =>
        node is YamlSequenceNode seq ? seq.Children.OfType<YamlScalarNode>().Select(s => s.Value ?? "") : [];
}
