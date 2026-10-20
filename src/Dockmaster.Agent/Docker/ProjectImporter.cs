using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dockmaster.Agent.Docker;

/// <summary>
/// Takes over a compose project that was started outside Dockmaster. In order of preference:
/// 1. the compose files named in the com.docker.compose.* labels, normalised by `docker compose config --no-interpolate`
///    (${VARS} stay, relative paths become absolute host paths so volumes keep pointing at the same data);
/// 2. the same files when those paths only exist inside another container — Portainer stacks record
///    /data/compose/N/... — translated to host paths through that container's mounts;
/// 3. a compose file rebuilt from the running containers (<see cref="ComposeReconstructor"/>).
/// Host files are read by a short-lived helper container from the agent's own image (it ships the docker CLI),
/// because the agent container itself does not see the host file system.
/// </summary>
public sealed class ProjectImporter
{
    private readonly DockerService _docker;
    private readonly ComposeRunner _compose;
    private readonly ILogger<ProjectImporter> _log;

    public ProjectImporter(DockerService docker, ComposeRunner compose, ILogger<ProjectImporter> log)
    {
        _docker = docker;
        _compose = compose;
        _log = log;
    }

    public sealed record Imported(string Compose, string Env, string Source, string[] ConfigFiles, string[] Warnings);

    private sealed record Location(string WorkingDir, string[] Files, string[] EnvFiles);

    public async Task<Imported> ImportAsync(string project, Func<string, Task> log, CancellationToken ct)
    {
        var all = await _docker.Api.ListContainersAsync(true, ct);
        var mine = all.Where(c => c.Labels != null && c.Labels.TryGetValue(DockerService.ProjectLabel, out var p) && p == project).ToList();
        if (mine.Count == 0) throw new InvalidOperationException($"no containers of compose project '{project}' on this host");

        var labels = mine.Select(c => c.Labels).FirstOrDefault(l => l!.ContainsKey(DockerService.WorkingDirLabel)) ?? mine[0].Labels!;
        string[] Split(string key) => labels.Get(key).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var original = new Location(labels.Get(DockerService.WorkingDirLabel), Split(DockerService.ConfigFilesLabel), Split(DockerService.EnvironmentFileLabel));

        var warnings = new List<string>();
        var image = await _docker.OwnImageAsync(ct);
        var attempts = new List<string>();

        foreach (var (candidate, label) in Candidates(original, all))
        {
            var (loc, how) = (candidate, label); // reassigned below when the file is found elsewhere in its folder
            await log($"trying {how}: {string.Join(", ", loc.Files)}");
            var (ok, compose, err) = await ConfigAsync(project, loc, image, ct);
            if (!ok)
            {
                attempts.Add($"{how} ({string.Join(", ", loc.Files)}): {Short(err)}");
                await log("  " + err);
                // the recorded file is gone but its folder may still hold the project (Portainer moves stacks between vN folders)
                if (loc.WorkingDir.Length == 0 || await ProbeAsync(loc, image, ct) is not { } found) continue;
                await log($"found {found} in {loc.WorkingDir}");
                loc = loc with { Files = [found] };
                how += ", newest compose file in its folder";
                (ok, compose, err) = await ConfigAsync(project, loc, image, ct);
                if (!ok) { attempts.Add($"{how} ({found}): {Short(err)}"); continue; }
            }
            var env = await EnvAsync(loc, image, ct);
            if (loc.EnvFiles.Length > 1) warnings.Add($"only the first env file ({loc.EnvFiles[0]}) was imported");
            if (EnvRecovery.HasVariables(compose)) env = await RecoverEnvAsync(project, loc, image, mine, env, warnings, ct);
            if (compose.Contains("\n    build:")) warnings.Add("some services have a build: section — they are deployed with the image that is already on the host; pull & deploy skips them");
            _log.LogInformation("imported compose project {Project} ({How})", project, how);
            return new Imported(compose, env, how, loc.Files, warnings.ToArray());
        }

        // 3. nothing readable: rebuild from the containers
        await log("compose files not readable — rebuilding the compose file from the running containers");
        var inspected = new List<(ContainerInspectResponse, ImageInspectResponse?)>();
        foreach (var c in mine)
        {
            var i = await _docker.InspectAsync(c.ID, ct);
            inspected.Add((i, await _docker.TryInspectImageAsync(i.Image, ct)));
        }
        warnings.Add("the original compose files could not be read; the compose file was rebuilt from the running containers — review it (volumes, networks, environment) before deploying");
        warnings.AddRange(attempts.Count > 0 ? attempts.Select(a => "tried " + a) : ["the containers carry no compose file labels"]);
        return new Imported(ComposeReconstructor.Build(project, inspected), "", "rebuilt from containers", original.Files, warnings.ToArray());
    }

    /// <summary>The label paths first, then the same paths translated through other containers' mounts.</summary>
    private static IEnumerable<(Location, string)> Candidates(Location original, IList<ContainerListResponse> all)
    {
        if (original.Files.Length == 0) yield break;
        yield return (original, "compose files from the labels");

        var mounts = all.SelectMany(c => (c.Mounts ?? []).Where(m => !string.IsNullOrEmpty(m.Source) && m.Destination.Length > 1)
                .Select(m => (c: c.DisplayName(), m)))
            .OrderByDescending(x => x.m.Destination.Length).ToList();
        foreach (var (owner, m) in mounts)
        {
            var dest = m.Destination.TrimEnd('/');
            string? Map(string p) => p == dest ? m.Source : p.StartsWith(dest + "/") ? m.Source.TrimEnd('/') + p[dest.Length..] : null;
            if (original.Files.Any(f => Map(f) == null)) continue;
            yield return (new Location(Map(original.WorkingDir) ?? original.WorkingDir, original.Files.Select(f => Map(f)!).ToArray(),
                original.EnvFiles.Select(e => Map(e) ?? e).ToArray()), $"paths inside container '{owner}' mapped to the host");
        }
    }

    /// <summary>Fills in ${VARIABLES} that have no value in the .env from what the running containers received.</summary>
    private async Task<string> RecoverEnvAsync(string project, Location loc, string? image, List<ContainerListResponse> mine, string env, List<string> warnings, CancellationToken ct)
    {
        var (ok, json, _) = await ConfigAsync(project, loc, image, ct, json: true);
        if (!ok) return env;
        JsonObject? config;
        try { config = JsonNode.Parse(json[Math.Max(0, json.IndexOf('{'))..]) as JsonObject; }
        catch (JsonException ex) { warnings.Add($"could not read the resolved compose file to recover variables: {ex.Message}"); return env; }
        if (config == null) return env;
        var known = env.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#') && l.Contains('='))
            .Select(l => l[..l.IndexOf('=')].Trim()).ToHashSet();

        var actual = new Dictionary<string, EnvRecovery.Actual>();
        foreach (var c in mine.Where(c => c.Labels!.ContainsKey(DockerService.ServiceLabel)).GroupBy(c => c.Labels![DockerService.ServiceLabel]))
        {
            var i = await _docker.InspectAsync(c.First().ID, ct);
            var envMap = new Dictionary<string, string>();
            foreach (var e in i.Config.Env ?? [])
            {
                var eq = e.IndexOf('=');
                if (eq > 0) envMap.TryAdd(e[..eq], e[(eq + 1)..]);
            }
            actual[c.Key] = new EnvRecovery.Actual(envMap, i.Config.Image, i.Config.Cmd ?? [], i.Config.Entrypoint ?? [], i.Config.Healthcheck?.Test ?? []);
        }

        var r = EnvRecovery.Recover(config, actual, known);
        if (r.Values.Count > 0)
        {
            var sb = new StringBuilder(env.TrimEnd());
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("# recovered from the running containers on import\n");
            foreach (var (k, v) in r.Values.OrderBy(kv => kv.Key)) sb.Append(EnvRecovery.Line(k, v)).Append('\n');
            env = sb.ToString();
            warnings.Add($"{r.Values.Count} variable(s) had no value in an env file and were recovered from the running containers: {string.Join(", ", r.Values.Keys.Order())}");
        }
        if (r.Unresolved.Count > 0)
            warnings.Add($"no value found for {string.Join(", ", r.Unresolved)} — set them on the .env tab (or they fall back to their defaults / empty)");
        return env;
    }

    private async Task<(bool ok, string compose, string error)> ConfigAsync(string project, Location loc, string? image, CancellationToken ct, bool json = false)
    {
        var args = new List<string> { "compose", "-p", project };
        if (loc.WorkingDir.Length > 0) args.AddRange(["--project-directory", loc.WorkingDir]);
        foreach (var f in loc.Files) args.AddRange(["-f", f]);
        foreach (var e in loc.EnvFiles) args.AddRange(["--env-file", e]);
        args.AddRange(["config", "--no-interpolate"]);
        if (json) args.AddRange(["--format", "json"]);
        var (code, stdout, stderr) = await HelperAsync(loc, image, "docker", args, ct);
        return code == 0 ? (true, stdout, "") : (false, "", stderr.Trim());
    }

    /// <summary>A compose file in the working dir or its newest vN sub-folder, or null.</summary>
    private async Task<string?> ProbeAsync(Location loc, string? image, CancellationToken ct)
    {
        var wd = loc.WorkingDir.TrimEnd('/');
        var (code, stdout, _) = await HelperAsync(loc with { Files = [], EnvFiles = [] }, image, "find", [wd, "-maxdepth", "2", "-type", "f", "-name", "*compose*.y*ml"], ct);
        if (code != 0) return null;
        static int Version(string dir) => Path.GetFileName(dir) is ['v', .. var n] && int.TryParse(n, out var v) ? v : -1;
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => !Path.GetFileName(f).StartsWith("docker-compose.dockmaster", StringComparison.Ordinal))
            .OrderByDescending(f => Path.GetDirectoryName(f) == wd ? 0 : Version(Path.GetDirectoryName(f)!) + 1)
            .ThenBy(f => Path.GetFileName(f) switch { "docker-compose.yml" => 0, "compose.yaml" => 1, "compose.yml" => 2, "docker-compose.yaml" => 3, _ => 9 })
            .FirstOrDefault();
    }

    private async Task<string> EnvAsync(Location loc, string? image, CancellationToken ct)
    {
        var path = loc.EnvFiles.Length > 0 ? loc.EnvFiles[0] : loc.WorkingDir.Length > 0 ? Path.Combine(loc.WorkingDir, ".env") : null;
        if (path == null) return "";
        var (code, stdout, _) = await HelperAsync(loc, image, "cat", [path], ct);
        return code == 0 ? stdout : "";
    }

    /// <summary>Runs a command that sees the project's host directories (read-only, at the same paths).</summary>
    private Task<(int code, string stdout, string stderr)> HelperAsync(Location loc, string? image, string entrypoint, IEnumerable<string> args, CancellationToken ct)
    {
        if (image == null) return _compose.CaptureAsync(entrypoint, args, ct); // agent runs on the host itself
        var dirs = loc.Files.Concat(loc.EnvFiles).Select(Path.GetDirectoryName).Append(loc.WorkingDir)
            .Where(d => !string.IsNullOrEmpty(d)).Distinct().ToList();
        var run = new List<string> { "run", "--rm", "--network", "none", "--entrypoint", entrypoint };
        // --mount instead of -v: a missing source is an error, -v would create the directory on the host
        foreach (var d in dirs) run.AddRange(["--mount", $"type=bind,source={d},target={d},readonly"]);
        run.Add(image);
        run.AddRange(args);
        return _compose.CaptureAsync("docker", run, ct);
    }

    private static string Short(string err)
    {
        var line = DockerErrors.LastLine(err) ?? err;
        return line.Length > 200 ? line[..200] + "…" : line;
    }

    public static string ToPayload(Imported i) => JsonSerializer.Serialize(i, AgentJson.Web);
}
