using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dockmaster.Agent.Docker;

/// <summary>
/// Recovers values of ${VARIABLES} a compose file uses from the containers it created. Portainer, for example,
/// keeps stack variables in its own database, so an imported stack has the references but no values.
/// For every environment entry, image, command, entrypoint and healthcheck of a service, the template ("Host=${DB_HOST};Pwd=${DB_PASS}") is matched
/// against the value the running container actually got, which yields the variables.
/// </summary>
public static partial class EnvRecovery
{
    public sealed record Result(Dictionary<string, string> Values, List<string> Unresolved);

    /// <summary>What a running container actually got.</summary>
    public sealed record Actual(Dictionary<string, string> Env, string Image, IList<string> Command, IList<string> Entrypoint, IList<string> Healthcheck);

    /// <param name="config">`docker compose config --no-interpolate --format json` output</param>
    /// <param name="actual">per service: what its first container runs with</param>
    /// <param name="known">variables that already have a value (.env)</param>
    public static Result Recover(JsonObject config, IReadOnlyDictionary<string, Actual> actual, ISet<string> known)
    {
        var values = new Dictionary<string, string>();
        var referenced = new SortedSet<string>();
        foreach (var (name, node) in config["services"]?.AsObject() ?? new JsonObject())
        {
            if (node is not JsonObject svc) continue;
            var templates = new List<(string template, string? value)>();
            actual.TryGetValue(name, out var real);
            if (svc["environment"] is JsonObject env)
                foreach (var (key, v) in env)
                    if (v?.ToString() is { } t) templates.Add((t, real?.Env.GetValueOrDefault(key)));
            if (svc["image"]?.ToString() is { } img) templates.Add((img, real?.Image));
            // command / entrypoint / healthcheck: element by element, e.g. ["redis-server", "--requirepass", "${REDIS_PASSWORD}"]
            AddList(templates, svc["command"], real?.Command);
            AddList(templates, svc["entrypoint"], real?.Entrypoint);
            AddList(templates, svc["healthcheck"]?["test"], real?.Healthcheck);

            foreach (var (template, value) in templates)
            {
                var vars = VarRegex().Matches(Unescape(template)).Select(Name).ToList();
                if (vars.Count == 0) continue;
                referenced.UnionWith(vars);
                if (value == null || vars.All(v => known.Contains(v) || values.ContainsKey(v))) continue;
                if (Match(template, value) is { } found)
                    foreach (var (k, v) in found)
                        if (!known.Contains(k)) values.TryAdd(k, v);
            }
        }
        // variables used elsewhere (ports, volumes, labels...) are only reported
        foreach (Match m in VarRegex().Matches(Unescape(config.ToJsonString()))) referenced.Add(Name(m));
        var unresolved = referenced.Where(v => !known.Contains(v) && !values.ContainsKey(v)).ToList();
        return new Result(values, unresolved);
    }

    /// <summary>"$$" is compose's escape for a literal "$" — "$${1}" is not a variable.</summary>
    private static string Unescape(string s) => s.Replace("$$", "\u0000");

    private static void AddList(List<(string, string?)> templates, JsonNode? node, IList<string>? real)
    {
        var items = node switch
        {
            JsonArray a => a.Select(x => x?.ToString() ?? "").ToList(),
            JsonValue v => [v.ToString()],
            _ => null,
        };
        if (items == null || real == null || real.Count == 0) return;
        if (items.Count == real.Count)
            for (var i = 0; i < items.Count; i++) templates.Add((items[i], real[i]));
        else templates.Add((string.Join(' ', items), string.Join(' ', real))); // shell form vs. split form
    }

    /// <summary>Matches "a${X}b${Y:-d}c" against a concrete string, returning X and Y.</summary>
    public static Dictionary<string, string>? Match(string template, string value)
    {
        var pattern = new StringBuilder("^");
        var names = new List<string>();
        var last = 0;
        var t = Unescape(template);
        foreach (Match m in VarRegex().Matches(t))
        {
            pattern.Append(Regex.Escape(t[last..m.Index].Replace("\u0000", "$")));
            names.Add(Name(m));
            pattern.Append($"(?<v{names.Count - 1}>.*?)");
            last = m.Index + m.Length;
        }
        pattern.Append(Regex.Escape(t[last..].Replace("\u0000", "$"))).Append('$');
        var match = Regex.Match(value, pattern.ToString(), RegexOptions.Singleline);
        if (!match.Success) return null;
        var result = new Dictionary<string, string>();
        for (var i = 0; i < names.Count; i++)
        {
            var v = match.Groups[$"v{i}"].Value;
            if (result.TryGetValue(names[i], out var prev) && prev != v) return null; // same variable, two values
            result[names[i]] = v;
        }
        return result;
    }

    /// <summary>A .env line; values with spaces, quotes or '#' are quoted so compose reads them back unchanged.</summary>
    public static string Line(string key, string value)
    {
        if (value.Length == 0 || SafeRegex().IsMatch(value)) return $"{key}={value}";
        if (!value.Contains('\'')) return $"{key}='{value}'";
        return $"{key}=\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "$$")}\"";
    }

    /// <summary>A compose file that uses variables ("${X}", "${X:-d}" or "$X").</summary>
    public static bool HasVariables(string compose) => VarRegex().IsMatch(Unescape(compose));

    private static string Name(Match m) => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;

    // ${X}, ${X:-default} and the unbraced $X
    [GeneratedRegex(@"\$(?:\{(\w+)(?:[:?+-][^}]*)?\}|([A-Za-z_]\w*))")] private static partial Regex VarRegex();
    [GeneratedRegex(@"^[A-Za-z0-9_./:@,+=-]+$")] private static partial Regex SafeRegex();
}
