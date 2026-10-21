using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Dockmaster.Agent.Docker;

/// <summary>How one service of a deploy is rolled out, read from its compose spec (see <see cref="Deployer"/>).</summary>
/// <param name="Scalable">
/// replicas can run side by side (no container_name, no fixed host port): a new replica count only adds or removes
/// replicas, whatever the update strategy
/// </param>
/// <param name="Job">x-dockmaster.schedule: a job the server starts on its schedule — a deploy creates its container without starting it</param>
internal sealed record ServicePlan(string Name, int Replicas, bool Rolling, bool Scalable, bool HasHealthcheck, int Parallelism, TimeSpan Delay, TimeSpan Monitor,
    string? PreStopHook, TimeSpan Timeout, bool DrainUntilUnhealthy, TimeSpan DrainGrace, bool Job = false);

/// <summary>Reading a compose project the way `docker compose config --format json` writes it, plus Dockmaster's x-dockmaster fields.</summary>
internal static partial class ComposeSpec
{
    private const string PreStopHookLabel = "dockmaster.pre-stop-hook";
    private const string LegacyPreStopHookLabel = "docker-rollout.pre-stop-hook";
    private static readonly TimeSpan DefaultDrainGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// x-dockmaster of each service, read from the compose file as written: `docker compose config` keeps extension
    /// fields at the top level but drops them inside services. Values come back as strings.
    /// </summary>
    public static Dictionary<string, JsonObject> ServiceExtensions(string yaml)
    {
        var result = new Dictionary<string, JsonObject>();
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.FirstOrDefault()?.RootNode is not YamlMappingNode root
                || !root.Children.TryGetValue(new YamlScalarNode("services"), out var node) || node is not YamlMappingNode services) return result;
            foreach (var (key, value) in services.Children)
            {
                if (key is not YamlScalarNode { Value: { } svc } || value is not YamlMappingNode spec
                    || !spec.Children.TryGetValue(new YamlScalarNode("x-dockmaster"), out var x) || x is not YamlMappingNode ext) continue;
                var o = new JsonObject();
                foreach (var (k, v) in ext.Children)
                    if (k is YamlScalarNode { Value: { } name } && v is YamlScalarNode val) o[name] = val.Value;
                result[svc] = o;
            }
        }
        catch (YamlException) { } // compose config has already accepted the file; nothing to add then
        return result;
    }

    /// <summary>
    /// What a service needs running before it starts, read from the compose file as written: each service it depends
    /// on, with its depends_on condition ("service_started" where it has none, and for what compose adds by itself:
    /// network_mode service:x, volumes_from, links). Null when the file cannot say for sure: no such service, extends
    /// or include (they bring in services from elsewhere), a name made of variables, or a file that does not parse.
    /// </summary>
    public static Dictionary<string, string>? Dependencies(string yaml, string service)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.FirstOrDefault()?.RootNode is not YamlMappingNode root || root.Children.ContainsKey(new YamlScalarNode("include"))
                || !root.Children.TryGetValue(new YamlScalarNode("services"), out var node) || node is not YamlMappingNode services
                || !services.Children.TryGetValue(new YamlScalarNode(service), out var s) || s is not YamlMappingNode spec
                || spec.Children.ContainsKey(new YamlScalarNode("extends"))) return null;
            var deps = new Dictionary<string, string>();
            YamlNode? Get(string key) => spec.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v : null;
            switch (Get("depends_on"))
            {
                case YamlSequenceNode list:
                    foreach (var d in list.OfType<YamlScalarNode>()) deps[d.Value ?? ""] = "service_started";
                    break;
                case YamlMappingNode map:
                    foreach (var (k, v) in map.Children)
                        deps[(k as YamlScalarNode)?.Value ?? ""] = v is YamlMappingNode m && m.Children.TryGetValue(new YamlScalarNode("condition"), out var c)
                            ? (c as YamlScalarNode)?.Value ?? "" : "service_started";
                    break;
            }
            if (Get("network_mode") is YamlScalarNode { Value: { } mode } && mode.StartsWith("service:")) deps.TryAdd(mode["service:".Length..], "service_started");
            if (Get("volumes_from") is YamlSequenceNode from)
                foreach (var v in from.OfType<YamlScalarNode>().Select(v => v.Value ?? "").Where(v => !v.StartsWith("container:")))
                    deps.TryAdd(v.Split(':')[0], "service_started");
            if (Get("links") is YamlSequenceNode links)
                foreach (var l in links.OfType<YamlScalarNode>()) deps.TryAdd((l.Value ?? "").Split(':')[0], "service_started");
            return deps.Keys.Any(k => k.Length == 0 || k.Contains('$')) ? null : deps;
        }
        catch (YamlException) { return null; }
    }

    public static int? ParseInt(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var n) ? n : int.TryParse(node?.ToString(), out var p) ? p : null;

    /// <summary>depends_on as `docker compose config` writes it (a map), or the short list form.</summary>
    public static IEnumerable<string> DependsOn(JsonObject spec) => spec["depends_on"] switch
    {
        JsonObject map => map.Select(d => d.Key),
        JsonArray list => list.Select(d => d?.ToString() ?? "").Where(d => d != ""),
        _ => [],
    };

    /// <summary>The plan of one service: its resolved compose spec, the deploy's timeout and its x-dockmaster fields.</summary>
    public static ServicePlan Plan(string name, JsonObject spec, TimeSpan timeout, JsonObject? ext)
    {
        var deploy = spec["deploy"]?.AsObject();
        var update = deploy?["update_config"]?.AsObject();
        var replicas = deploy?["replicas"]?.GetValue<int>() ?? 1;
        // a fixed host port can be held by one container only; a port docker picks per container ("ip::80", as the
        // server publishes replicas behind a balancer) does not stand in the way of old and new running side by side
        var fixedPorts = spec["ports"]?.AsArray().Count(p => p?["published"] != null) ?? 0;
        var rollingFlag = bool.TryParse(ext?["rolling"]?.ToString(), out var rf) ? rf : (bool?)null;
        // a job runs one container at a time, started by the server: never rolled or scaled
        var job = !string.IsNullOrWhiteSpace(ext?["schedule"]?.ToString());
        var scalable = !job && spec["container_name"] == null && fixedPorts == 0;
        var rolling = scalable && (rollingFlag ?? replicas > 1);
        var labels = spec["labels"]?.AsObject();
        return new ServicePlan(
            name,
            replicas,
            rolling,
            scalable,
            spec["healthcheck"] != null && spec["healthcheck"]?["disable"]?.GetValue<bool>() != true,
            Math.Max(1, update?["parallelism"]?.GetValue<int>() ?? 1),
            ParseDuration(update?["delay"]) ?? TimeSpan.Zero,
            ParseDuration(update?["monitor"]) ?? ParseDuration(ext?["monitor"]) ?? Readiness.Monitor,
            labels?[PreStopHookLabel]?.GetValue<string>() ?? labels?[LegacyPreStopHookLabel]?.GetValue<string>(),
            timeout,
            // x-dockmaster: {drain: unhealthy}: after the pre-stop hook wait for the old replica's healthcheck to fail
            // (the load balancer drops unhealthy containers) instead of a fixed sleep in the hook
            ext?["drain"]?.ToString() == "unhealthy",
            ParseDuration(ext?["drain_grace"]) ?? DefaultDrainGrace,
            job);
    }

    /// <summary>Compose durations: "35s", "1m30s", "500ms", or a plain number of seconds/nanoseconds.</summary>
    private static TimeSpan? ParseDuration(JsonNode? node)
    {
        if (node == null) return null;
        if (node is JsonValue v && v.TryGetValue<long>(out var n)) return n > 1_000_000 ? TimeSpan.FromTicks(n / 100) : TimeSpan.FromSeconds(n);
        var s = node.ToString().Trim();
        if (s.Length == 0) return null;
        var total = TimeSpan.Zero;
        var any = false;
        foreach (Match m in DurationRegex().Matches(s))
        {
            any = true;
            var val = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            total += m.Groups[2].Value switch
            {
                "ms" => TimeSpan.FromMilliseconds(val),
                "s" => TimeSpan.FromSeconds(val),
                "m" => TimeSpan.FromMinutes(val),
                "h" => TimeSpan.FromHours(val),
                _ => TimeSpan.FromSeconds(val),
            };
        }
        return any ? total : null;
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(ms|s|m|h)?")] private static partial Regex DurationRegex();
}
