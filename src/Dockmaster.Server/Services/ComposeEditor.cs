using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Dockmaster.Server.Services;

/// <summary>
/// Surgical edits of a compose file: replaces or inserts single scalar values in place, so comments, ordering and
/// formatting survive (a YAML load/save round trip would drop them). Only block-style mappings are edited.
/// </summary>
public static partial class ComposeEditor
{
    /// <param name="Ports">several replicas and a fixed host port: <see cref="PortsBalance"/> or <see cref="PortsRange"/>, null leaves the ports alone</param>
    public sealed record ServiceLimits(int? Replicas, string? Memory, string? Cpus, string? Ports = null);

    public const string PortsBalance = "balance";
    public const string PortsRange = "range";

    /// <summary>The least memory docker accepts as a limit: 6 MiB.</summary>
    private const long MinMemory = 6L << 20;

    /// <summary>
    /// Runs an edit of <paramref name="yaml"/>; a refusal (a shape that cannot be edited in place, a file that is not
    /// valid YAML, or a result that would be broken) comes back as its message instead of an exception.
    /// </summary>
    /// <param name="adds">the service the edit adds</param>
    /// <param name="removes">the service the edit takes out</param>
    public static bool TryEdit(string yaml, Func<string, string> edit, out string edited, [NotNullWhen(false)] out string? error, string? adds = null, string? removes = null)
    {
        try
        {
            edited = edit(yaml);
            error = Broken(yaml, edited, adds, removes);
        }
        catch (Exception ex) when (ex is InvalidOperationException or YamlException)
        {
            edited = "";
            error = ex.Message;
        }
        return error == null;
    }

    /// <summary>
    /// The safety net under every edit in place: what an edit wrote must still parse, with the same services (but the
    /// one it adds or takes out). Null when it does.
    /// </summary>
    public static string? Broken(string before, string after, string? adds = null, string? removes = null)
    {
        static HashSet<string> Names(string yaml) =>
            (YamlNodes.LoadRoot(yaml).Child("services") as YamlMappingNode)?.Children.Keys.Select(k => k.Text()).ToHashSet() ?? [];
        HashSet<string> now;
        try { now = Names(after); }
        catch (Exception ex) when (ex is InvalidOperationException or YamlException)
        {
            return $"the change would break the compose file ({ex.Message}); edit the compose file";
        }
        var expected = Names(before);
        if (adds != null) expected.Add(adds);
        if (removes != null) expected.Remove(removes);
        return now.SetEquals(expected) ? null : "the change would change other services of the compose file; edit the compose file";
    }

    public static string? Validate(ServiceLimits l)
    {
        if (l.Replicas is < 0 or > 100) return "replicas must be between 0 and 100";
        if (l.Ports is not (null or PortsBalance or PortsRange)) return $"ports is {PortsBalance} or {PortsRange}";
        if (!string.IsNullOrWhiteSpace(l.Memory))
        {
            var mem = l.Memory.Trim();
            if (!MemoryRegex().IsMatch(mem)) return "memory limit looks like 512M, 1.5G or 268435456";
            // a bare number is bytes, which is almost always a forgotten unit
            if (ParseMemory(mem) < MinMemory)
                return char.IsDigit(mem[^1]) ? $"memory limit {mem} is in bytes — did you mean {mem}M?" : "memory limit must be at least 6M";
        }
        if (!string.IsNullOrWhiteSpace(l.Cpus) && (!double.TryParse(l.Cpus.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var c) || c <= 0 || c > 1024))
            return "CPU limit is a number of cores, e.g. 0.5 or 2";
        return null;
    }

    /// <summary>Sets deploy.replicas and the memory / CPU limits of one service. null leaves a value alone, "" removes the limit.</summary>
    public static string SetLimits(string yaml, string service, ServiceLimits l)
    {
        if (l.Replicas is { } r)
        {
            if (r > 1) yaml = MakeRoom(yaml, service, r, l.Ports);
            yaml = Set(yaml, service, ["deploy", "replicas"], r.ToString(CultureInfo.InvariantCulture));
        }
        // legacy service-level keys win when present (that is what the file already uses)
        if (l.Memory != null)
            yaml = SetOrRemove(yaml, service, Has(yaml, service, ["mem_limit"]) ? ["mem_limit"] : ["deploy", "resources", "limits", "memory"], l.Memory);
        if (l.Cpus != null)
            yaml = SetOrRemove(yaml, service, Has(yaml, service, ["cpus"]) ? ["cpus"] : ["deploy", "resources", "limits", "cpus"], l.Cpus, quote: true);
        return yaml;
    }

    /// <summary>
    /// Several replicas cannot share one container_name (compose refuses the file): it goes, the replicas get the
    /// names compose gives them and are reached by the service name. The UI says so before it asks for replicas.
    /// </summary>
    private static string DropContainerName(string yaml, string service) => Remove(yaml, service, ["container_name"]);

    /// <summary>
    /// What several replicas need: no container_name, and (as the person chose) a balancer on the fixed ports or
    /// ranges wide enough for <paramref name="replicas"/>. See <see cref="PortBalancer"/>.
    /// </summary>
    public static string MakeRoom(string yaml, string service, int replicas, string? ports)
    {
        yaml = DropContainerName(yaml, service);
        if (ports == PortsBalance)
        {
            if (PortBalancer.Narrow(Service(yaml, service)) is { } single) yaml = SetSequence(yaml, service, "ports", single);
            return Set(yaml, service, ["x-dockmaster", "balance"], "true");
        }
        if (ports != PortsRange) return yaml;
        yaml = Remove(yaml, service, ["x-dockmaster", "balance"]);
        var svc = Service(yaml, service);
        var list = PortBalancer.WidenRanges(svc, replicas, out var problem) ?? throw new InvalidOperationException($"{service}: {problem}");
        // written as it was when every range is wide enough already
        return list.SequenceEqual(svc.Child("ports").Scalars().Select(p => PortEntry.Parse(p).Short())) ? yaml : SetSequence(yaml, service, "ports", list);
    }

    /// <summary>"512M", "1.5g", "268435456", "300mb" -> bytes (binary units, like docker).</summary>
    public static long ParseMemory(string s)
    {
        var m = MemoryPartsRegex().Match(s.Trim());
        if (!m.Success) throw new FormatException("bad memory value: " + s);
        var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var mult = char.ToLowerInvariant(m.Groups[2].Value.FirstOrDefault()) switch { 'k' => 1L << 10, 'm' => 1L << 20, 'g' => 1L << 30, _ => 1L };
        return (long)Math.Round(n * mult);
    }

    public static double ParseCpus(string s) => double.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);

    public static readonly string[] RestartPolicies = ["no", "always", "unless-stopped", "on-failure"];

    /// <summary>
    /// The service's `restart:`. "no" is written quoted (bare, YAML reads it as false) and explicitly, so a
    /// deploy.restart_policy further down cannot take over; on-failure carries its retry limit as on-failure:N.
    /// </summary>
    public static string SetRestart(string yaml, string service, string policy, int maxRetries) =>
        Set(yaml, service, ["restart"], policy switch
        {
            "no" => "\"no\"",
            "on-failure" when maxRetries > 0 => $"on-failure:{maxRetries}",
            _ => policy,
        });

    /// <summary>One label of a service in its `labels:` mapping (added when missing).</summary>
    public static string SetLabel(string yaml, string service, string key, string value) => Set(yaml, service, ["labels", key], Quote(value));

    /// <summary>
    /// A new tag for a service's image, the rest of the reference as it is ("registry:5000/app:1.2" → "…/app:1.3"; a
    /// digest goes). Refused for an image made of ${VARIABLES}: set the variable in the .env instead.
    /// </summary>
    public static string SetImageTag(string yaml, string service, string tag)
    {
        if (!TagRegex().IsMatch(tag)) throw new InvalidOperationException($"'{tag}' is not an image tag: letters, digits, '_', '.' and '-'");
        var image = Service(yaml, service).Scalar("image") ?? throw new InvalidOperationException($"service '{service}' has no image");
        if (image.Contains('$')) throw new InvalidOperationException($"the image of '{service}' is written with variables ({image}): set the variable in the .env instead");
        var name = image.Split('@')[0];
        var slash = name.LastIndexOf('/');
        var colon = name.LastIndexOf(':');
        if (colon > slash) name = name[..colon];
        return Set(yaml, service, ["image"], name + ":" + tag);
    }

    /// <summary>
    /// The servers a service runs on: none = the project's default server, one = x-dockmaster.agent, several =
    /// x-dockmaster.agents (its replicas are spread over them).
    /// </summary>
    public static string SetPlacement(string yaml, string service, IReadOnlyList<string> agentNames)
    {
        var names = agentNames.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        yaml = Remove(yaml, service, ["x-dockmaster", "agents"]);
        if (names.Count != 1) yaml = Remove(yaml, service, ["x-dockmaster", "agent"]);
        return names.Count switch
        {
            0 => yaml,
            1 => Set(yaml, service, ["x-dockmaster", "agent"], Quote(names[0])),
            _ => Set(yaml, service, ["x-dockmaster", "agents"], "[" + string.Join(", ", names.Select(Quote)) + "]"),
        };
    }

    /// <summary>x-dockmaster.autoscale of a service: written key by key (comments around stay), null removes it.</summary>
    /// <param name="ports">see <see cref="ServiceLimits.Ports"/></param>
    public static string SetAutoscale(string yaml, string service, AutoscaleSpec? spec, string? ports = null)
    {
        string[] At(string key) => ["x-dockmaster", "autoscale", key];
        foreach (var key in new[] { "agents", "mode", "cpu", "max" }) yaml = Remove(yaml, service, At(key));
        if (spec == null) return yaml;
        // mode auto starts more replicas than the file says
        if (spec.Auto && spec.Max > 1) yaml = MakeRoom(yaml, service, spec.Max, ports);
        yaml = Set(yaml, service, At("max"), spec.Max.ToString(CultureInfo.InvariantCulture));
        yaml = Set(yaml, service, At("cpu"), spec.Cpu.ToString(CultureInfo.InvariantCulture));
        if (spec.AgentNames.Length > 0) yaml = Set(yaml, service, At("agents"), "[" + string.Join(", ", spec.AgentNames.Select(Quote)) + "]");
        return Set(yaml, service, At("mode"), spec.Mode);
    }

    /// <summary>
    /// x-dockmaster.schedule (with timezone and timeout) of a service: a job Dockmaster starts on the schedule. Written
    /// key by key, null removes it. A job must not be restarted by docker when it exits (it would run again at once):
    /// `restart:` becomes "no" unless it is on-failure (retries of a failed run, as a job may want); a job runs one
    /// container at a time, so a replica count above one is refused. Taking the schedule off leaves `restart:` alone
    /// — the form sends the policy it wants next.
    /// </summary>
    public static string SetSchedule(string yaml, string service, ScheduleSpec? spec)
    {
        string[] At(string key) => ["x-dockmaster", key];
        var svc = Service(yaml, service);
        if (spec != null && int.TryParse(svc.Scalar("deploy", "replicas"), out var replicas) && replicas > 1)
            throw new InvalidOperationException($"'{service}' has {replicas} replicas: a job runs one container at a time, set the replicas to 1 first");
        foreach (var key in new[] { "timeout", "timezone", "schedule" }) yaml = Remove(yaml, service, At(key));
        if (spec == null) return yaml;
        yaml = Set(yaml, service, At("schedule"), Quote(spec.Cron));
        if (spec.TimeZone is { Length: > 0 } tz) yaml = Set(yaml, service, At("timezone"), Quote(tz));
        if (spec.Timeout is { Length: > 0 } timeout) yaml = Set(yaml, service, At("timeout"), timeout);
        var restart = (Service(yaml, service).Scalar("restart") ?? "").Trim().Trim('"', '\'');
        return restart is "no" or "false" || restart.StartsWith("on-failure") ? yaml : SetRestart(yaml, service, "no", 0);
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// Replaces a service's list of scalars (expose, extra_hosts…) in place: a block list keeps its indent, a flow
    /// list `[a, b]` stays a flow list, a missing key is added at the end of the service, an empty list removes it.
    /// </summary>
    public static string SetSequence(string yaml, string service, string key, IReadOnlyList<string> items)
    {
        var map = Service(yaml, service);
        if (Entry(map, key, $"'{key}' of '{service}'") is not (var keyNode, var node))
        {
            if (items.Count == 0) return yaml;
            if (map.Style == MappingStyle.Flow) throw new InvalidOperationException("the compose file uses an inline mapping here; edit the compose file");
            var (pos, prefix, indent) = AppendPoint(yaml, map);
            return yaml.Insert(pos, prefix + SequenceBlock(key, items, indent, yaml));
        }
        if (node is YamlSequenceNode { Style: SequenceStyle.Flow } flow && items.Count > 0)
            return yaml[..(int)flow.Start.Index] + "[" + string.Join(", ", items.Select(Quote)) + "]" + yaml[FlowEnd(yaml, flow)..];
        if (node is not YamlSequenceNode && !node.IsEmptyValue())
            throw new InvalidOperationException($"'{key}' of '{service}' is not a list; edit the compose file");
        var end = LineEndAfter(yaml, ContentEnd(node));
        var keyIndent = (int)keyNode.Start.Column - 1;
        return yaml[..LineStart(yaml, keyNode)] + (items.Count == 0 ? "" : SequenceBlock(key, items, keyIndent, yaml)) + yaml[end..];
    }

    private static string SequenceBlock(string key, IReadOnlyList<string> items, int indent, string yaml)
    {
        var nl = NewLine(yaml);
        var sb = new StringBuilder();
        sb.Append(' ', indent).Append(key).Append(':').Append(nl);
        foreach (var item in items) sb.Append(' ', indent + 2).Append("- ").Append(Quote(item)).Append(nl);
        return sb.ToString();
    }

    private static string SetOrRemove(string yaml, string service, string[] path, string? value, bool quote = false)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value)) return Remove(yaml, service, path);
        return Set(yaml, service, path, quote ? $"\"{value}\"" : value);
    }

    private static YamlMappingNode Service(string yaml, string service)
    {
        if (YamlNodes.LoadRoot(yaml).Child("services") is not YamlMappingNode services)
            throw new InvalidOperationException("compose file has no services");
        return Entry(services, service, $"service '{service}'")?.Value as YamlMappingNode
            ?? throw new InvalidOperationException($"service '{service}' not found");
    }

    /// <summary>
    /// A key of a mapping and its value, null when the key is missing. Refused when the value is a YAML alias
    /// (`key: *name`) or anchor (`key: &amp;name …`) — YamlDotNet hands back the anchored node, whose text sits
    /// elsewhere or is shared with whatever uses it, so an edit in place would land in the wrong spot or change them
    /// all — and when a missing key comes from a merge (`&lt;&lt;: *base`): a new one would replace what the merge brings.
    /// </summary>
    private static (YamlNode Key, YamlNode Value)? Entry(YamlMappingNode map, string key, string what)
    {
        var k = map.Children.Keys.FirstOrDefault(x => x.Equals(new YamlScalarNode(key)));
        if (k == null)
        {
            var merged = map.Children.TryGetValue(new YamlScalarNode("<<"), out var m) ? m is YamlSequenceNode seq ? seq.Children : [m] : [];
            if (merged.OfType<YamlMappingNode>().Any(x => x.Children.ContainsKey(new YamlScalarNode(key))))
                throw new InvalidOperationException($"{what} comes from a YAML merge (<<: *…); edit the compose file");
            return null;
        }
        var value = map.Children[k];
        if (value.Start.Index < k.End.Index) throw new InvalidOperationException($"{what} is a YAML alias (*…); edit the compose file");
        if (!value.Anchor.IsEmpty) throw new InvalidOperationException($"{what} is a YAML anchor (&{value.Anchor}) other parts of the file may share; edit the compose file");
        return (k, value);
    }

    private static bool Has(string yaml, string service, string[] path) => Service(yaml, service).Child(path) != null;

    private static string Set(string yaml, string service, string[] path, string value)
    {
        var map = Service(yaml, service);
        for (var i = 0; i < path.Length; i++)
        {
            if (Entry(map, path[i], $"'{string.Join('.', path[..(i + 1)])}' of '{service}'") is not (var keyNode, var next))
                return Insert(yaml, map, path[i..], value);
            if (i == path.Length - 1)
            {
                if (next is not YamlScalarNode s || s.Style is ScalarStyle.Literal or ScalarStyle.Folded)
                    throw new InvalidOperationException($"'{string.Join('.', path)}' of '{service}' is not a plain value; edit the compose file");
                if (string.IsNullOrEmpty(s.Value) && s.Start.Index == s.End.Index)
                    return yaml.Insert((int)s.Start.Index, " " + value); // "key:" with an empty value
                return yaml[..(int)s.Start.Index] + value + yaml[(int)s.End.Index..];
            }
            if (next.IsEmptyValue())
            {
                // "deploy:" with nothing under it -> replace the empty value by a nested block
                return ReplaceWithBlock(yaml, keyNode, (YamlScalarNode)next, path[(i + 1)..], value);
            }
            map = next as YamlMappingNode ?? throw new InvalidOperationException($"'{path[i]}' of '{service}' is not a mapping; edit the compose file");
        }
        return yaml;
    }

    private static string Insert(string yaml, YamlMappingNode parent, string[] path, string value)
    {
        if (parent.Style == MappingStyle.Flow || parent.Children.Count == 0)
            throw new InvalidOperationException("the compose file uses an inline mapping here; edit the compose file");
        var (pos, prefix, indent) = AppendPoint(yaml, parent);
        return yaml.Insert(pos, prefix + Block(path, value, indent, yaml));
    }

    /// <summary>
    /// Where a new key goes at the end of a block mapping: after the line its last value ends on, at the indent of its
    /// keys, with a newline first when the file ends without one.
    /// </summary>
    private static (int Pos, string Prefix, int Indent) AppendPoint(string yaml, YamlMappingNode map)
    {
        var pos = LineEndAfter(yaml, ContentEnd(map));
        var prefix = pos == yaml.Length && yaml.Length > 0 && yaml[^1] != '\n' ? NewLine(yaml) : "";
        return (pos, prefix, (int)map.Children.Keys.First().Start.Column - 1);
    }

    /// <summary>Index just after the `]` of a flow list (YamlDotNet ends the node where it starts).</summary>
    private static int FlowEnd(string yaml, YamlSequenceNode flow) =>
        yaml.IndexOf(']', flow.Children.Count > 0 ? ContentEnd(flow.Children[^1]) : (int)flow.Start.Index) + 1;

    /// <summary>End of the last scalar inside a node (block collections themselves only end where the next key starts).</summary>
    private static int ContentEnd(YamlNode node) => node switch
    {
        YamlMappingNode m when m.Children.Count > 0 => m.Children.Max(kv => Math.Max((int)kv.Key.End.Index, ContentEnd(kv.Value))),
        YamlSequenceNode q when q.Children.Count > 0 => q.Children.Max(ContentEnd),
        _ => (int)node.End.Index,
    };

    private static string ReplaceWithBlock(string yaml, YamlNode keyNode, YamlScalarNode empty, string[] path, string value)
    {
        var indent = (int)keyNode.Start.Column - 1 + 2;
        var pos = LineEndAfter(yaml, (int)Math.Max(keyNode.End.Index, empty.End.Index));
        // drop an explicit "~" / "null" so the key opens a block
        var line = yaml[(int)keyNode.Start.Index..pos];
        var fixedLine = NullValueRegex().Replace(line, ":");
        yaml = yaml[..(int)keyNode.Start.Index] + fixedLine + yaml[pos..];
        pos = (int)keyNode.Start.Index + fixedLine.Length;
        return yaml.Insert(pos, Block(path, value, indent, yaml));
    }

    private static string Block(string[] path, string value, int indent, string yaml)
    {
        var nl = NewLine(yaml);
        var sb = new StringBuilder();
        for (var i = 0; i < path.Length; i++)
        {
            sb.Append(' ', indent + 2 * i).Append(path[i]).Append(':');
            sb.Append(i == path.Length - 1 ? " " + value : "").Append(nl);
        }
        return sb.ToString();
    }

    private static string NewLine(string yaml) => yaml.Contains("\r\n") ? "\r\n" : "\n";

    /// <summary>Index of the first character of the line <paramref name="node"/> starts on.</summary>
    private static int LineStart(string yaml, YamlNode node) => yaml.LastIndexOf('\n', Math.Max(0, (int)node.Start.Index - 1)) + 1;

    /// <summary>Index just after the newline that ends the line containing <paramref name="index"/> (or end of text).</summary>
    private static int LineEndAfter(string yaml, int index)
    {
        var nl = yaml.IndexOf('\n', Math.Min(index, yaml.Length));
        return nl < 0 ? yaml.Length : nl + 1;
    }

    private static string Remove(string yaml, string service, string[] path)
    {
        YamlNode cur = Service(yaml, service);
        YamlNode? keyNode = null;
        foreach (var key in path)
        {
            if (cur is not YamlMappingNode m) return yaml;
            if (Entry(m, key, $"'{string.Join('.', path)}' of '{service}'") is not (var k, var v)) return yaml; // nothing to remove
            keyNode = k;
            cur = v;
        }
        if (keyNode == null || cur is not (YamlScalarNode or YamlSequenceNode)) return yaml;
        yaml = yaml[..LineStart(yaml, keyNode)] + yaml[LineEndAfter(yaml, ContentEnd(cur))..];
        // "limits:" with nothing left under it is invalid for compose: drop emptied parents, innermost first
        for (var depth = path.Length - 1; depth > 0; depth--)
        {
            var parent = path[..depth];
            YamlNode node = Service(yaml, service);
            YamlNode? parentKey = null;
            foreach (var key in parent)
            {
                if (node is not YamlMappingNode m || m.Children.Keys.FirstOrDefault(x => x.Equals(new YamlScalarNode(key))) is not { } k) return yaml;
                parentKey = k;
                node = m.Children[k];
            }
            var empty = node is YamlMappingNode { Children.Count: 0 } || node.IsEmptyValue();
            if (!empty || parentKey == null) return yaml;
            yaml = yaml[..LineStart(yaml, parentKey)] + yaml[LineEndAfter(yaml, ContentEnd(node))..];
        }
        return yaml;
    }

    [GeneratedRegex(@"^\d+(\.\d+)?\s*([bkmgBKMG]|[kmgKMG][bB])?$")] private static partial Regex MemoryRegex();
    /// <summary>An image tag; project files are named by the same rule (nothing a path, a shell or YAML reads specially).</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$")] internal static partial Regex TagRegex();
    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*([bkmgBKMG]?)[bB]?$")] private static partial Regex MemoryPartsRegex();
    [GeneratedRegex(@":\s*(~|null|Null|NULL)\s*(?=(#|\r?\n|$))")] private static partial Regex NullValueRegex();
}
