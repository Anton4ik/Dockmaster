using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Dockmaster.Server.Services;

/// <param name="FixedPorts">ports published on a fixed host port: one container at a time can hold one</param>
/// <param name="AgentNames">
/// the servers this service runs on (x-dockmaster.agent, or x-dockmaster.agents to spread its replicas); empty = the
/// project's default server
/// </param>
/// <param name="Expose">the service's `expose` list: the ports other services (on any server of the project) may use</param>
/// <param name="Autoscale">x-dockmaster.autoscale, null = the replica count stays as written</param>
/// <param name="Mounts">the service's `volumes:`: where its data lives</param>
/// <param name="Secrets">the secrets it reads (its `secrets:` list, by name)</param>
public sealed record ServiceSummary(string Name, string Image, int Replicas, string? MemoryLimit, string? CpuLimit, bool HasHealthcheck, bool HasContainerName, int Ports,
    int FixedPorts, bool? RollingFlag, string? UpdateDelay, string? Monitor, int Parallelism, string[] AgentNames, string[] Expose, AutoscaleSpec? Autoscale = null,
    MountSummary[]? Mounts = null, string[]? Secrets = null)
{
    /// <summary>The published ports as written ("8080:80"); null when one is written with options (only the compose file can change them).</summary>
    public string[]? PortList { get; init; }
    /// <summary>The environment as written (${VARIABLES} unresolved); null when it cannot be shown as name = value.</summary>
    public EnvVar[]? Environment { get; init; }
    /// <summary>`restart:` as written; null when the file does not say.</summary>
    public string? Restart { get; init; }
    /// <summary>`container_name:` as written; null when compose names the containers.</summary>
    public string? ContainerName { get; init; }
    /// <summary>x-dockmaster.balance: a balancer holds the fixed ports and spreads the connections over the replicas.</summary>
    public bool Balance { get; init; }
    /// <summary>x-dockmaster.schedule: a job Dockmaster starts on a cron schedule, which does its work and exits; null = a service that keeps running.</summary>
    public ScheduleSpec? Schedule { get; init; }
    /// <summary>A job on a schedule (see <see cref="Schedule"/>).</summary>
    public bool IsJob => Schedule != null;
    /// <summary>
    /// The most replicas the fixed host ports let run on one server (the smallest range, 1 for a single port); null
    /// when nothing limits them (no fixed port, or a balancer holds them).
    /// </summary>
    public int? PortRoom { get; init; }
    public MountSummary[] Mounts { get; init; } = Mounts ?? [];
    public string[] Secrets { get; init; } = Secrets ?? [];

    /// <summary>
    /// True when the agent will do a zero-downtime rolling update for this service (same rule as the agent, which
    /// sees the file after the balancer took the fixed ports over).
    /// </summary>
    public bool Rolling => !HasContainerName && (FixedPorts == 0 || Balance && PortRoom == null) && (RollingFlag ?? Replicas > 1);
    /// <summary>How the agent decides a new replica is ready.</summary>
    public string Readiness => HasHealthcheck ? "healthcheck" : $"running {Monitor ?? "10s"}";
}

/// <summary>
/// x-dockmaster.autoscale of a service: more replicas while the ones running use more than <paramref name="Cpu"/> % of
/// their CPU limit, back to deploy.replicas when they calm down.
/// </summary>
/// <param name="Max">the most replicas; deploy.replicas is the least</param>
/// <param name="Cpu">average CPU of the replicas, in % of their limit, above which one more is started</param>
/// <param name="AgentNames">servers extra replicas may go to; empty = the servers the service runs on</param>
/// <param name="Auto">true (mode: auto) starts and removes replicas; false (mode: recommend) only says what it would do</param>
public sealed record AutoscaleSpec(int Max, int Cpu, string[] AgentNames, bool Auto)
{
    public const int DefaultCpu = 70;
    public const string ModeAuto = "auto";
    public const string ModeRecommend = "recommend";
    public string Mode => Auto ? ModeAuto : ModeRecommend;
}

/// <summary>What the UI and the deploy need to know about a compose file's services (variables unresolved).</summary>
public static partial class ComposeServices
{
    /// <summary>
    /// Best-effort parse for the UI table: a file that does not parse has no services. With the project's name the
    /// mounts carry the names docker gives their volumes.
    /// </summary>
    public static List<ServiceSummary> Parse(string yaml, string? project = null)
    {
        // parsed again on every list of the projects (polled every few seconds), every autoscaler minute and every
        // health check: a file changes rarely, so the last files parsed are kept by their text
        var key = (yaml, project);
        if (_parsed.TryGetValue(key, out var hit)) return new List<ServiceSummary>(hit);
        var list = ParseUncached(yaml, project);
        if (_parsed.Count >= ParsedCacheSize) _parsed.Clear();
        _parsed[key] = list.AsReadOnly();
        return new List<ServiceSummary>(list);
    }

    private const int ParsedCacheSize = 256;
    private static readonly ConcurrentDictionary<(string, string?), IReadOnlyList<ServiceSummary>> _parsed = new();

    private static List<ServiceSummary> ParseUncached(string yaml, string? project)
    {
        var list = new List<ServiceSummary>();
        try
        {
            var root = YamlNodes.LoadRoot(yaml);
            if (root.Child("services") is not YamlMappingNode services) return list;
            foreach (var (k, v) in services.Children)
            {
                if (v is not YamlMappingNode s) continue;
                list.Add(new ServiceSummary(
                    k.Text(),
                    s.Scalar("image") ?? "",
                    int.TryParse(s.Scalar("deploy", "replicas"), out var replicas) ? replicas : 1,
                    s.Scalar("deploy", "resources", "limits", "memory") ?? s.Scalar("mem_limit"),
                    s.Scalar("deploy", "resources", "limits", "cpus") ?? s.Scalar("cpus"),
                    s.Child("healthcheck") != null && s.Scalar("healthcheck", "disable") != "true",
                    s.Child("container_name") != null,
                    s.Child("ports") is YamlSequenceNode ports ? ports.Children.Count : 0,
                    s.Child("ports") is YamlSequenceNode fixedPorts ? fixedPorts.Children.Count(IsFixedPort) : 0,
                    s.Flag("x-dockmaster", "rolling"),
                    s.Scalar("deploy", "update_config", "delay"),
                    s.Scalar("deploy", "update_config", "monitor") ?? s.Scalar("x-dockmaster", "monitor"),
                    int.TryParse(s.Scalar("deploy", "update_config", "parallelism"), out var parallelism) ? Math.Max(1, parallelism) : 1,
                    AgentNames(s),
                    s.Child("expose").Scalars().Where(p => p.Length > 0).ToArray(),
                    Autoscale(s),
                    ComposeMounts.Parse(root, s, project),
                    SecretNames(s))
                {
                    PortList = s.Child("ports") switch
                    {
                        null => [],
                        YamlSequenceNode seq when seq.Children.All(c => c is YamlScalarNode) => seq.Scalars().ToArray(),
                        _ => null,
                    },
                    Environment = EnvironmentOf(s),
                    Restart = s.Scalar("restart"),
                    ContainerName = s.Scalar("container_name"),
                    Balance = PortBalancer.Enabled(s),
                    PortRoom = PortRoom(s),
                    Schedule = Schedule(s),
                });
            }
        }
        catch { }
        return list;
    }

    /// <summary>See <see cref="ServiceSummary.PortRoom"/>: a balancer takes the single TCP ports, ranges and the rest stay.</summary>
    private static int? PortRoom(YamlMappingNode s)
    {
        var balanced = PortBalancer.Enabled(s) ? PortBalancer.Balanced(s) : [];
        var rooms = (s.Child("ports") as YamlSequenceNode)?.Children.Select(PortEntry.Parse).OfType<PortEntry>()
            .Where(p => !balanced.Contains(p)).Select(p => p.Room).OfType<int>().ToList() ?? [];
        return rooms.Count == 0 ? null : rooms.Min();
    }

    /// <summary>`environment:` as a mapping or a list of KEY=value; null when it holds something else.</summary>
    private static EnvVar[]? EnvironmentOf(YamlMappingNode s) => s.Child("environment") switch
    {
        null => [],
        YamlMappingNode m when m.Children.Values.All(v => v is YamlScalarNode) =>
            m.Children.Select(kv => new EnvVar(kv.Key.Text(), (kv.Value as YamlScalarNode)?.Value ?? "")).ToArray(),
        YamlSequenceNode q when q.Children.All(c => c is YamlScalarNode) =>
            q.Scalars().Select(x => x.IndexOf('=') is var eq && eq > 0 ? new EnvVar(x[..eq], x[(eq + 1)..]) : new EnvVar(x, "")).ToArray(),
        _ => null,
    };

    /// <summary>`secrets: [a, b]` or `secrets: [{source: a, target: …}]`: the names.</summary>
    public static string[] SecretNames(YamlMappingNode? service) =>
        service.Child("secrets") is YamlSequenceNode seq
            ? seq.Children.Select(i => i is YamlScalarNode sc ? sc.Value : (i as YamlMappingNode).Scalar("source")).OfType<string>().Where(n => n.Length > 0).ToArray()
            : [];

    /// <summary>x-dockmaster.agents (a list: the replicas are spread over them) or x-dockmaster.agent (one server).</summary>
    public static string[] AgentNames(YamlMappingNode? service)
    {
        var names = service.Child("x-dockmaster", "agents").Scalars().ToList();
        if (names.Count == 0 && service.Scalar("x-dockmaster", "agent") is { } one) names.Add(one);
        return names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// A service as the file and the .env have it, apart from its replica count and x-dockmaster (which a deploy of a
    /// different count does not change): equal fingerprints, nothing new to roll out. Null when it cannot be read.
    /// </summary>
    public static string? Fingerprint(string yaml, string service, string env)
    {
        try
        {
            if (YamlNodes.LoadRoot(yaml).Child("services", service) is not YamlMappingNode svc) return null;
            svc.Children.Remove(new YamlScalarNode("x-dockmaster"));
            if (svc.Child("deploy") is YamlMappingNode deploy) deploy.Children.Remove(new YamlScalarNode("replicas"));
            var text = new StringWriter();
            new YamlStream(new YamlDocument(svc)).Save(text, assignAnchors: false);
            var bytes = System.Text.Encoding.UTF8.GetBytes(text.ToString().Replace("\r\n", "\n") + "\n--env--\n" + env.Replace("\r\n", "\n"));
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
        }
        catch { return null; }
    }

    /// <summary>x-dockmaster.autoscale: {max: 4, cpu: 70, agents: [a, b], mode: recommend|auto}; null when absent or without a usable max.</summary>
    public static AutoscaleSpec? Autoscale(YamlMappingNode? service)
    {
        if (service.Child("x-dockmaster", "autoscale") is not YamlMappingNode a) return null;
        if (!int.TryParse(a.Scalar("max"), out var max) || max < 1) return null;
        var cpu = int.TryParse(a.Scalar("cpu")?.TrimEnd('%'), out var c) ? Math.Clamp(c, 10, 95) : AutoscaleSpec.DefaultCpu;
        var agents = a.Child("agents").Scalars().Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new AutoscaleSpec(max, cpu, agents, string.Equals(a.Scalar("mode"), AutoscaleSpec.ModeAuto, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// x-dockmaster.schedule (a cron expression) with timezone and timeout next to it; null when absent or not a cron
    /// expression this server reads.
    /// </summary>
    public static ScheduleSpec? Schedule(YamlMappingNode? service)
    {
        if (service.Scalar("x-dockmaster", "schedule") is not { } cron || !Schedules.TryParse(cron, out _)) return null;
        var tz = service.Scalar("x-dockmaster", "timezone")?.Trim();
        var timeout = service.Scalar("x-dockmaster", "timeout")?.Trim();
        return new ScheduleSpec(cron.Trim(), string.IsNullOrEmpty(tz) ? null : tz, string.IsNullOrEmpty(timeout) ? null : timeout);
    }

    /// <summary>"8080:80", "10.0.0.1:8080:80", {published: 8080}: a fixed host port; "80" and "10.0.0.1::80" let docker pick one.</summary>
    private static bool IsFixedPort(YamlNode port) => port switch
    {
        YamlMappingNode m => m.Scalar("published") is { Length: > 0 },
        YamlScalarNode { Value: { } v } => v.Split('/')[0].Split(':') switch
        {
            [var hostPort, _] => hostPort.Length > 0,
            [_, var hostPort, _] => hostPort.Length > 0,
            _ => false,
        },
        _ => false,
    };

    /// <summary>
    /// ${VAR}, ${VAR:-default}, ${VAR-default} and $VAR replaced from a .env text, the way compose reads it (a
    /// variable the .env does not set takes its default, or stays as written); $$ is a literal $.
    /// </summary>
    public static string Interpolate(string text, string env)
    {
        if (!text.Contains('$')) return text;
        var vars = new Dictionary<string, string>();
        foreach (var raw in env.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (line.StartsWith('#') || eq <= 0) continue;
            vars[line[..eq].Trim()] = line[(eq + 1)..].Trim().Trim('"', '\'');
        }
        return VariableRegex().Replace(text, m =>
        {
            if (m.Value == "$$") return "$";
            var name = m.Groups["braced"].Success ? m.Groups["braced"].Value : m.Groups["plain"].Value;
            if (vars.TryGetValue(name, out var v) && (v.Length > 0 || m.Groups["op"].Value != ":-")) return v;
            return m.Groups["default"].Success ? m.Groups["default"].Value : m.Value;
        });
    }

    /// <summary>Image references of the image: lines (variables unresolved).</summary>
    public static List<string> ImageRefs(string yaml) =>
        ImageLineRegex().Matches(yaml).Select(m => m.Groups[1].Value.Trim().Trim('"', '\'')).Distinct().ToList();

    [GeneratedRegex(@"^\s*image:\s*(.+?)\s*(#.*)?$", RegexOptions.Multiline)] private static partial Regex ImageLineRegex();
    [GeneratedRegex(@"\$\$|\$\{(?<braced>[A-Za-z_][A-Za-z0-9_]*)(?:(?<op>:?-)(?<default>[^}]*))?\}|\$(?<plain>[A-Za-z_][A-Za-z0-9_]*)")] private static partial Regex VariableRegex();
}
