using System.Text;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Dockmaster.Server.Services;

/// <summary>
/// The values of the services' `environment:` (inline passwords, tokens) taken out of a compose file for users without
/// the Secrets permission, and put back when such a user saves the file: they see and send <see cref="Hidden"/>, the
/// stored value stays. Everything else in the file (comments, layout) is left as written.
/// </summary>
public static class ComposeEnv
{
    /// <summary>What a hidden value reads as (a plain YAML scalar, so the file stays valid).</summary>
    public const string Hidden = "(hidden)";

    /// <summary>Shown instead of a file that does not parse: its values cannot be told from the rest.</summary>
    public const string Unreadable = "# the compose file does not parse: it is shown with the Secrets permission\n";

    /// <param name="Item">a list item KEY=value (else the value of KEY in a mapping)</param>
    private sealed record Value(string Service, string Name, YamlScalarNode Node, bool Item)
    {
        public string Text => Item ? Node.Value!.Split('=', 2)[1] : Node.Value ?? "";
    }

    /// <summary>The file with every non-empty environment value as <see cref="Hidden"/>.</summary>
    public static string Redact(string yaml)
    {
        List<Value> values;
        try { values = Values(yaml); }
        catch (Exception ex) when (ex is YamlException or InvalidOperationException) { return yaml.Trim().Length == 0 ? yaml : Unreadable; }
        return Replace(yaml, values.Where(v => v.Text.Length > 0 && v.Text != Hidden), v => Write(v, Hidden));
    }

    /// <summary>
    /// <paramref name="edited"/> (sent by a user who saw <paramref name="original"/> redacted) with the stored values in
    /// place of <see cref="Hidden"/>: the original text when the value is written the same way, quoted otherwise.
    /// Error when a hidden value has no stored one to go back to (its service or name was changed).
    /// </summary>
    public static (string? Yaml, string? Error) Restore(string edited, string original)
    {
        if (edited == Unreadable) return (original, null);
        List<Value> now, before;
        try { now = Values(edited); }
        catch (Exception ex) when (ex is YamlException or InvalidOperationException) { return (edited, null); } // refused later like any broken file
        try { before = Values(original); }
        catch (Exception ex) when (ex is YamlException or InvalidOperationException) { before = []; }
        var stored = before.GroupBy(v => (v.Service, v.Name)).ToDictionary(g => g.Key, g => g.First());
        var hidden = now.Where(v => v.Text == Hidden).ToList();
        if (hidden.FirstOrDefault(v => !stored.ContainsKey((v.Service, v.Name))) is { } lost)
            return (null, $"service {lost.Service}: {lost.Name} is {Hidden} and has no stored value — renaming or moving it takes the Secrets permission");
        return (Replace(edited, hidden, v =>
        {
            var old = stored[(v.Service, v.Name)];
            // as written before; a block scalar's lines only fit where its key is indented as before
            return old.Item == v.Item && old.Text != Hidden && Source(original, old.Node) is var src
                && (!src.Contains('\n') || Indent(original, (int)old.Node.Start.Index) == Indent(edited, (int)v.Node.Start.Index))
                ? src
                : Write(v, old.Text);
        }), null);
    }

    private static int Indent(string text, int at)
    {
        var line = text.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
        var i = line;
        while (i < text.Length && text[i] == ' ') i++;
        return i - line;
    }

    /// <summary>Hides the values of a service's environment as the API lists them.</summary>
    public static EnvVar[]? Redact(EnvVar[]? env) => env?.Select(e => e.Value.Length > 0 ? e with { Value = Hidden } : e).ToArray();

    /// <summary>The service form's environment with the stored values in place of <see cref="Hidden"/>.</summary>
    public static List<EnvVar> Restore(List<EnvVar> sent, EnvVar[]? stored) =>
        sent.Select(e => e.Value == Hidden && stored?.FirstOrDefault(s => s.Name == e.Name) is { } s ? e with { Value = s.Value } : e).ToList();

    private static List<Value> Values(string yaml)
    {
        var values = new List<Value>();
        if (YamlNodes.LoadRoot(yaml).Child("services") is not YamlMappingNode services) return values;
        foreach (var (k, v) in services.Children)
            if (v is YamlMappingNode svc) Collect(k.Text(), svc.Child("environment"), values, 0);
        return values;
    }

    private static void Collect(string service, YamlNode? node, List<Value> values, int depth)
    {
        if (depth > 8) return;
        switch (node)
        {
            case YamlMappingNode m:
                foreach (var (k, v) in m.Children)
                {
                    // `<<: *defaults` merges another mapping (or a list of them) in
                    if (k.Text() == "<<") foreach (var merged in v is YamlSequenceNode q ? q.Children : [v]) Collect(service, merged, values, depth + 1);
                    else if (v is YamlScalarNode s) values.Add(new(service, k.Text(), s, false));
                }
                break;
            case YamlSequenceNode seq:
                foreach (var item in seq.Children)
                    if (item is YamlScalarNode { Value: { } text } s && text.IndexOf('=') > 0) values.Add(new(service, text[..text.IndexOf('=')], s, true));
                break;
        }
    }

    /// <summary>A value written as a YAML scalar: plain when it is <see cref="Hidden"/>, double-quoted (JSON is valid YAML) otherwise.</summary>
    private static string Write(Value v, string value)
    {
        var text = v.Item ? $"{v.Name}={value}" : value;
        return text == Hidden ? text : JsonSerializer.Serialize(text);
    }

    private static string Source(string yaml, YamlScalarNode node)
    {
        var (start, end) = Span(yaml, node);
        return yaml[start..end];
    }

    /// <summary>
    /// Where the scalar's value is written: after its anchor or tag (&amp;pw, !!str, which stay), without the whitespace
    /// after it (a block scalar ends with its line break).
    /// </summary>
    private static (int Start, int End) Span(string yaml, YamlScalarNode node)
    {
        var start = (int)node.Start.Index;
        var end = (int)node.End.Index;
        while (start < end && yaml[start] is '&' or '!')
        {
            while (start < end && !char.IsWhiteSpace(yaml[start])) start++;
            while (start < end && char.IsWhiteSpace(yaml[start])) start++;
        }
        while (end > start && char.IsWhiteSpace(yaml[end - 1])) end--;
        return (start, end);
    }

    /// <summary>The text with each value's scalar replaced.</summary>
    private static string Replace(string yaml, IEnumerable<Value> values, Func<Value, string> with)
    {
        var sb = new StringBuilder(yaml);
        // an anchored value reached from several services is one place in the text
        foreach (var v in values.GroupBy(v => v.Node.Start.Index).Select(g => g.First()).OrderByDescending(v => v.Node.Start.Index))
        {
            var (start, end) = Span(yaml, v.Node);
            sb.Remove(start, end - start).Insert(start, with(v));
        }
        return sb.ToString();
    }
}
