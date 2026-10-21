using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dockmaster.Server.Data;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Dockmaster.Server.Services;

/// <summary>
/// A variable an instance of a template may set. It reaches the compose file as ${NAME} through the instance's .env,
/// where it is written single-quoted (literal): compose fills it in after the YAML is parsed, so a value can never add
/// keys, mounts or services — the pattern only decides what the template accepts.
/// </summary>
/// <param name="Pattern">a regular expression the whole value must match; empty = <see cref="Templates.DefaultPattern"/></param>
public sealed record TemplateVariable(string Name, string? Pattern = null, bool Required = false, string? Default = null, string? Description = null);

/// <summary>The rules of templates and their instances: names, variables, the .env an instance gets, API keys.</summary>
public static partial class Templates
{
    /// <summary>Set for every instance: its own name, and the template's.</summary>
    public const string InstanceVariable = "INSTANCE";
    public const string TemplateVariableName = "TEMPLATE";

    /// <summary>
    /// What a variable without a pattern of its own accepts: a word of letters, digits, '_', '.' and '-' (not starting
    /// with '.' or '-'). No '/' or ':', so a value can never name a host path, a port mapping or an image of its own
    /// choice in a spot like `${DATA}:/data`; anything wider needs a pattern the admin writes.
    /// </summary>
    public const string DefaultPattern = @"([A-Za-z0-9_][A-Za-z0-9_.-]{0,255})?";

    /// <summary>
    /// Keys of a service where a value chosen by whoever creates an instance decides what the container may reach:
    /// host paths, host ports, devices, the image, its privileges.
    /// </summary>
    private static readonly string[] SensitiveKeys =
        ["image", "volumes", "ports", "devices", "cap_add", "privileged", "network_mode", "pid", "security_opt", "volumes_from"];

    public const string KeyPrefix = "dmk_";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    public static string? ValidateTemplateName(string name) =>
        TemplateNameRegex().IsMatch(name) ? null : "template name must be lowercase letters, digits or '-', start with a letter, at most 30 characters";

    public static string? ValidateInstanceName(string name) =>
        InstanceNameRegex().IsMatch(name) ? null : "instance name must be lowercase letters, digits or '-', start with a letter or digit, at most 40 characters";

    /// <summary>The compose project an instance runs as.</summary>
    public static string ProjectName(string template, string instance) => $"{template}-{instance}";

    /// <summary>The instance's own name, from its project's name.</summary>
    public static string InstanceName(string template, string project) =>
        project.StartsWith(template + "-", StringComparison.Ordinal) ? project[(template.Length + 1)..] : project;

    public static List<TemplateVariable> ParseVariables(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<TemplateVariable>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string SerializeVariables(IEnumerable<TemplateVariable> variables) => JsonSerializer.Serialize(variables, Json);

    public static Dictionary<string, string> ParseValues(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string SerializeValues(IReadOnlyDictionary<string, string> values) =>
        JsonSerializer.Serialize(values.OrderBy(v => v.Key, StringComparer.Ordinal).ToDictionary(v => v.Key, v => v.Value), Json);

    /// <summary>The template's variable rules as an admin wrote them: the first problem, or null.</summary>
    public static string? ValidateVariables(IReadOnlyList<TemplateVariable> variables)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in variables)
        {
            if (!VariableNameRegex().IsMatch(v.Name ?? "")) return $"variable '{v.Name}': names are letters, digits and '_', starting with a letter or '_'";
            if (v.Name is InstanceVariable or TemplateVariableName) return $"{v.Name} is set by Dockmaster for every instance";
            if (!seen.Add(v.Name!)) return $"variable {v.Name} is listed twice";
            Regex pattern;
            try { pattern = Compile(v.Pattern); }
            catch (ArgumentException ex) { return $"variable {v.Name}: the pattern is not a valid regular expression ({ex.Message})"; }
            if (v.Default is { } d && Check(v, pattern, d) is { } bad) return $"variable {v.Name}: the default {bad}";
        }
        return null;
    }

    /// <summary>
    /// The values an instance runs with: the caller's, else the defaults, checked against the template. Unknown names
    /// and missing required ones are refused rather than ignored — a typo in the caller would otherwise go unnoticed.
    /// </summary>
    public static (Dictionary<string, string>? Values, string? Error) Resolve(IReadOnlyList<TemplateVariable> variables, IReadOnlyDictionary<string, string>? given)
    {
        given ??= new Dictionary<string, string>();
        if (given.Keys.FirstOrDefault(k => variables.All(v => v.Name != k)) is { } unknown)
            return (null, $"the template has no variable {unknown}");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in variables)
        {
            // a required variable set to "" is as good as missing
            if (!given.TryGetValue(v.Name, out var value) || value == null || value.Length == 0 && v.Required)
            {
                if (v.Default != null) value = v.Default;
                else if (v.Required) return (null, $"variable {v.Name} is required");
                else continue;
            }
            Regex pattern;
            // a pattern saved before patterns were checked on their own
            try { pattern = Compile(v.Pattern); }
            catch (ArgumentException) { return (null, $"variable {v.Name}: the template's pattern is not a valid regular expression — an admin fixes it"); }
            if (Check(v, pattern, value) is { } bad) return (null, $"variable {v.Name}: the value {bad}");
            values[v.Name] = value;
        }
        return (values, null);
    }

    /// <summary>
    /// The template's variables used where the caller's value decides what the container may reach (<see cref="SensitiveKeys"/>,
    /// and the top-level volumes, whose driver_opts can bind any host path): an API key holder picks those values, so
    /// the admin should check the variable's pattern. Nothing when the file does not parse.
    /// </summary>
    public static List<string> Warnings(string composeYaml, IReadOnlyList<TemplateVariable> variables)
    {
        var names = variables.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        var warnings = new List<string>();
        if (names.Count == 0) return warnings;
        YamlMappingNode root;
        try { root = YamlNodes.LoadRoot(composeYaml); }
        catch (Exception ex) when (ex is YamlException or InvalidOperationException) { return warnings; }
        IEnumerable<string> Used(YamlNode? node) =>
            AllScalars(node).SelectMany(s => VariableRefRegex().Matches(s).Select(m => m.Groups[1].Value)).Where(names.Contains).Distinct();
        void Warn(string where, IEnumerable<string> used) =>
            warnings.AddRange(used.Select(n => $"{where}: variable {n} decides it — whoever creates instances (an API key) picks its value, so make sure its pattern only allows what you mean"));

        if (root.Child("services") is YamlMappingNode services)
            foreach (var (k, v) in services.Children)
                if (v is YamlMappingNode svc)
                    foreach (var key in SensitiveKeys)
                        Warn($"service {k.Text()}, {key}", Used(svc.Child(key)));
        if (root.Child("volumes") is YamlMappingNode volumes)
            foreach (var (k, v) in volumes.Children) Warn($"volume {k.Text()}", Used(v));
        return warnings;
    }

    private static IEnumerable<string> AllScalars(YamlNode? node) => node switch
    {
        YamlScalarNode s => s.Value is { } v ? [v] : [],
        YamlSequenceNode q => q.Children.SelectMany(AllScalars),
        YamlMappingNode m => m.Children.SelectMany(kv => AllScalars(kv.Value)),
        _ => [],
    };

    /// <summary>The stored values of the variables the template still has.</summary>
    public static Dictionary<string, string> Known(IReadOnlyList<TemplateVariable> variables, IReadOnlyDictionary<string, string> values) =>
        values.Where(x => variables.Any(v => v.Name == x.Key)).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

    /// <summary>
    /// The .env of an instance: the template's, without lines for the names the instance sets, then the instance's
    /// values (single-quoted: compose takes them literally).
    /// </summary>
    public static string RenderEnv(string templateEnv, string template, string instance, IReadOnlyDictionary<string, string> values)
    {
        var mine = new Dictionary<string, string>(values, StringComparer.Ordinal) { [InstanceVariable] = instance, [TemplateVariableName] = template };
        var sb = new StringBuilder();
        foreach (var line in (templateEnv ?? "").Replace("\r", "").Split('\n'))
        {
            var key = line.Split('=', 2)[0].Trim();
            if (key.StartsWith("export ", StringComparison.Ordinal)) key = key[7..].Trim();
            if (mine.ContainsKey(key)) continue;
            sb.Append(line).Append('\n');
        }
        var env = sb.ToString().TrimEnd('\n');
        sb.Clear();
        if (env.Length > 0) sb.Append(env).Append("\n\n");
        sb.Append("# set by Dockmaster for this instance of template ").Append(template).Append('\n');
        foreach (var (k, v) in mine.OrderBy(x => x.Key, StringComparer.Ordinal)) sb.Append(k).Append("='").Append(v).Append("'\n");
        return sb.ToString();
    }

    /// <summary>A new API key and what is stored of it.</summary>
    public static (string Key, string Hash, string Hint) NewKey()
    {
        var key = KeyPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        return (key, HashKey(key), key[^4..]);
    }

    public static string HashKey(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    /// <summary>The server for a new instance: of the online ones it may go to, the one with the most memory free (then the fewest instances).</summary>
    public static int? PickAgent(IEnumerable<(int AgentId, double MemoryUsedShare, int Instances)> candidates) =>
        candidates.OrderBy(c => Math.Round(c.MemoryUsedShare, 2)).ThenBy(c => c.Instances).ThenBy(c => c.AgentId).Select(c => (int?)c.AgentId).FirstOrDefault();

    /// <summary>The pattern matching whole values. It is checked on its own first: wrapped, "a)|(.*" would compile and accept anything.</summary>
    private static Regex Compile(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) pattern = DefaultPattern;
        else _ = new Regex(pattern, RegexOptions.CultureInvariant, PatternTimeout);
        return new($@"\A(?:{pattern})\z", RegexOptions.CultureInvariant, PatternTimeout);
    }

    /// <summary>
    /// Why the value cannot be used, or null. Quotes and line breaks are refused whatever the pattern says: they would
    /// end the .env value. So is a backslash at the end: compose reads \' as a quote inside the value, even single-quoted,
    /// and the value would run on into the next line.
    /// </summary>
    private static string? Check(TemplateVariable v, Regex pattern, string value)
    {
        if (value.IndexOfAny(['\'', '\r', '\n', '\0']) >= 0) return "may not contain quotes or line breaks";
        if (value.EndsWith('\\')) return "may not end with a backslash";
        try { return pattern.IsMatch(value) ? null : $"does not match {(string.IsNullOrWhiteSpace(v.Pattern) ? "the allowed characters" : v.Pattern)}"; }
        catch (RegexMatchTimeoutException) { return "took too long to check against the pattern"; }
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,29}$")] private static partial Regex TemplateNameRegex();
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")] private static partial Regex InstanceNameRegex();
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")] private static partial Regex VariableNameRegex();
    // $NAME, ${NAME}, ${NAME:-x}
    [GeneratedRegex(@"\$\{?([A-Za-z_][A-Za-z0-9_]*)")] private static partial Regex VariableRefRegex();
}
