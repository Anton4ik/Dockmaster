using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dockmaster.Agent.Docker;

/// <summary>
/// Last resort of an import: rebuilds a compose file from the running containers of a project when its original
/// files are gone. Covers image, container_name, command/entrypoint, environment (minus the image's own defaults),
/// ports, volumes, networks, restart policy, healthcheck, user labels, replicas and memory / CPU limits.
/// </summary>
public static partial class ComposeReconstructor
{
    public static string Build(string project, IReadOnlyList<(ContainerInspectResponse c, ImageInspectResponse? image)> containers)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# reconstructed from the running containers of '{project}' — the original compose files were not found. Review before deploying.");
        sb.AppendLine("services:");
        var externalVolumes = new SortedSet<string>();
        var externalNetworks = new SortedSet<string>();

        foreach (var group in containers.GroupBy(x => Label(x.c, DockerService.ServiceLabel) ?? x.c.Name.TrimStart('/')).OrderBy(g => g.Key))
        {
            var (c, image) = group.OrderBy(x => x.c.Name).First();
            var cfg = c.Config;
            var host = c.HostConfig;
            sb.AppendLine($"  {Key(group.Key)}:");
            sb.AppendLine($"    image: {Q(cfg.Image)}");
            var name = c.Name.TrimStart('/');
            if (group.Count() == 1 && !DefaultNameRegex().IsMatch(name)) sb.AppendLine($"    container_name: {Q(name)}");

            if (cfg.Entrypoint is { Count: > 0 } ep && !Same(ep, image?.Config?.Entrypoint)) List(sb, "entrypoint", ep);
            if (cfg.Cmd is { Count: > 0 } cmd && !Same(cmd, image?.Config?.Cmd)) List(sb, "command", cmd);
            if (!string.IsNullOrEmpty(cfg.User) && cfg.User != image?.Config?.User) sb.AppendLine($"    user: {Q(cfg.User)}");
            if (!string.IsNullOrEmpty(cfg.WorkingDir) && cfg.WorkingDir != image?.Config?.WorkingDir) sb.AppendLine($"    working_dir: {Q(cfg.WorkingDir)}");

            var imageEnv = new HashSet<string>(image?.Config?.Env ?? []);
            var env = (cfg.Env ?? []).Where(e => !imageEnv.Contains(e)).ToList();
            if (env.Count > 0)
            {
                sb.AppendLine("    environment:");
                foreach (var e in env)
                {
                    var i = e.IndexOf('=');
                    sb.AppendLine(i < 0 ? $"      {Key(e)}: \"\"" : $"      {Key(e[..i])}: {Q(e[(i + 1)..])}");
                }
            }

            var ports = (host.PortBindings ?? [])
                .SelectMany(kv => (kv.Value ?? []).Select(b => (container: kv.Key, b))).ToList();
            if (ports.Count > 0)
            {
                sb.AppendLine("    ports:");
                foreach (var (container, b) in ports)
                {
                    var hostPart = string.IsNullOrEmpty(b.HostIP) || b.HostIP is "0.0.0.0" or "::" ? b.HostPort : $"{b.HostIP}:{b.HostPort}";
                    var cp = container.EndsWith("/tcp") ? container[..^4] : container;
                    sb.AppendLine($"      - {Q(string.IsNullOrEmpty(hostPart) ? cp : $"{hostPart}:{cp}")}");
                }
            }

            var mounts = (c.Mounts ?? []).Where(m => m.Type is "bind" or "volume").ToList();
            if (mounts.Count > 0)
            {
                sb.AppendLine("    volumes:");
                foreach (var m in mounts)
                {
                    var ro = m.RW ? "" : ":ro";
                    if (m.Type == "volume")
                    {
                        // anonymous volumes (64 hex chars) come back on their own from the image's VOLUME
                        if (HexRegex().IsMatch(m.Name)) continue;
                        externalVolumes.Add(m.Name);
                        sb.AppendLine($"      - {Q($"{m.Name}:{m.Destination}{ro}")}");
                    }
                    else sb.AppendLine($"      - {Q($"{m.Source}:{m.Destination}{ro}")}");
                }
            }

            var nets = c.NetworkSettings?.Networks?.Keys.ToList() ?? [];
            if (host.NetworkMode is "host" or "none") sb.AppendLine($"    network_mode: {host.NetworkMode}");
            else if (nets.Count > 0 && !(nets.Count == 1 && nets[0] == $"{project}_default"))
            {
                sb.AppendLine("    networks:");
                foreach (var n in nets)
                {
                    var key = n == $"{project}_default" ? "default" : n;
                    if (key != "default") externalNetworks.Add(n);
                    sb.AppendLine($"      - {Key(key)}");
                }
            }

            if (DockerService.RestartPolicyName(host.RestartPolicy) is var restart and not "no") sb.AppendLine($"    restart: {restart}");

            if (cfg.Healthcheck is { Test.Count: > 0 } hc && !Same(hc.Test, image?.Config?.Healthcheck?.Test))
            {
                sb.AppendLine("    healthcheck:");
                List(sb, "test", hc.Test, 6);
                // Docker gives nanoseconds
                if (hc.Interval > 0) sb.AppendLine($"      interval: {Dur(Nanos(hc.Interval))}");
                if (hc.Timeout > 0) sb.AppendLine($"      timeout: {Dur(Nanos(hc.Timeout))}");
                if (hc.Retries > 0) sb.AppendLine($"      retries: {hc.Retries}");
                if (hc.StartPeriod > 0) sb.AppendLine($"      start_period: {Dur(Nanos(hc.StartPeriod))}");
                if (hc.StartInterval > 0) sb.AppendLine($"      start_interval: {Dur(Nanos(hc.StartInterval))}");
            }

            var labels = (cfg.Labels ?? new Dictionary<string, string>())
                .Where(kv => !kv.Key.StartsWith("com.docker.compose.") && !kv.Key.StartsWith("org.opencontainers.")
                             && (image?.Config?.Labels == null || !image.Config.Labels.TryGetValue(kv.Key, out var v) || v != kv.Value)).ToList();
            if (labels.Count > 0)
            {
                sb.AppendLine("    labels:");
                foreach (var (k, v) in labels) sb.AppendLine($"      {Q(k)}: {Q(v)}");
            }

            var replicas = group.Count();
            var mem = host.Memory;
            var cpus = host.NanoCPUs / 1e9;
            if (replicas > 1 || mem > 0 || cpus > 0)
            {
                sb.AppendLine("    deploy:");
                if (replicas > 1) sb.AppendLine($"      replicas: {replicas}");
                if (mem > 0 || cpus > 0)
                {
                    sb.AppendLine("      resources:");
                    sb.AppendLine("        limits:");
                    if (mem > 0) sb.AppendLine($"          memory: {Mem(mem)}");
                    if (cpus > 0) sb.AppendLine($"          cpus: \"{cpus.ToString(CultureInfo.InvariantCulture)}\"");
                }
            }
        }

        if (externalVolumes.Count > 0)
        {
            sb.AppendLine("volumes:");
            foreach (var v in externalVolumes) sb.AppendLine($"  {Key(v)}:\n    external: true");
        }
        if (externalNetworks.Count > 0)
        {
            sb.AppendLine("networks:");
            foreach (var n in externalNetworks) sb.AppendLine($"  {Key(n)}:\n    external: true");
        }
        return sb.ToString();
    }

    private static string? Label(ContainerInspectResponse c, string key) => c.Config.Labels != null && c.Config.Labels.TryGetValue(key, out var v) ? v : null;

    private static bool Same(IList<string>? a, IList<string>? b) => a != null && b != null && a.SequenceEqual(b);

    private static void List(StringBuilder sb, string key, IList<string> items, int indent = 4) =>
        sb.Append(' ', indent).Append(key).Append(": [").Append(string.Join(", ", items.Select(Q))).AppendLine("]");

    /// <summary>
    /// A value: JSON strings are valid YAML scalars and need no further escaping rules — but compose reads "$" as the
    /// start of a variable, so a literal one is written "$$" (a password "ab$cd" must not become "ab").
    /// </summary>
    private static string Q(string s) => JsonSerializer.Serialize(s.Replace("$", "$$"));

    private static string Key(string k) => PlainKeyRegex().IsMatch(k) ? k : JsonSerializer.Serialize(k);

    private static TimeSpan Nanos(long ns) => TimeSpan.FromTicks(ns / 100);

    private static string Dur(TimeSpan t) => t.TotalSeconds % 1 == 0 ? $"{t.TotalSeconds:0}s" : $"{t.TotalMilliseconds:0}ms";

    private static string Mem(long bytes) =>
        bytes % (1L << 30) == 0 ? $"{bytes >> 30}G" : bytes % (1L << 20) == 0 ? $"{bytes >> 20}M" : bytes.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^[A-Za-z0-9._-]+-\d+$")] private static partial Regex DefaultNameRegex();
    [GeneratedRegex("^[0-9a-f]{64}$")] private static partial Regex HexRegex();
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.-]*$")] private static partial Regex PlainKeyRegex();
}
