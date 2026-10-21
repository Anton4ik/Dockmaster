using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Dockmaster.Protocol;

namespace Dockmaster.Agent.Docker;

/// <summary>Runs the docker CLI (`docker compose`, `docker login`, `docker pull`, helper `docker run`) as child processes.</summary>
public sealed partial class ComposeRunner
{
    private const string ComposeFile = "docker-compose.yml";
    private const string SecretsOverrideFile = "docker-compose.dockmaster-secrets.yml";
    private const string EnvFile = ".env";
    private const string FilesDir = "files";
    private const string SecretsDir = "secrets";
    private const string ManifestPrefix = ".dockmaster-";
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode Readable = OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private readonly AgentOptions _opts;
    private readonly ILogger<ComposeRunner> _log;

    public ComposeRunner(IOptions<AgentOptions> opts, ILogger<ComposeRunner> log)
    {
        _opts = opts.Value;
        _log = log;
    }

    /// <summary>Path of the Docker socket on the host, for helper containers that mount it.</summary>
    public string HostSocketPath => _opts.DockerSocket.StartsWith("unix://") ? _opts.DockerSocket["unix://".Length..] : "/var/run/docker.sock";

    /// <summary>
    /// Folder of a project's files. The server validates names too; checking here keeps a bad name from ever
    /// reaching the file system (files in it are written and removed).
    /// </summary>
    public string ProjectDir(string project)
    {
        if (!ProjectNameRegex().IsMatch(project)) throw new ArgumentException($"invalid project name '{project}'");
        return Path.Combine(_opts.DataDir, "projects", project);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$")] private static partial Regex ProjectNameRegex();

    /// <summary>
    /// Writes compose + env + secrets + the project's files, returns base args: compose -p project -f file
    /// [-f secrets override] [--env-file .env].
    /// </summary>
    public string[] PrepareProject(string project, string composeYaml, string? envFile, IReadOnlyDictionary<string, string> secrets,
        IReadOnlyDictionary<string, string>? files = null)
    {
        var dir = ProjectDir(project);
        RefuseLink(dir);
        Directory.CreateDirectory(dir);
        WriteReplacing(Path.Combine(dir, ComposeFile), composeYaml, null);
        WriteSecrets(dir, secrets);
        SyncDir(dir, FilesDir, files ?? new Dictionary<string, string>(), Readable);
        // passwords often live here
        if (!string.IsNullOrWhiteSpace(envFile)) WriteReplacing(Path.Combine(dir, EnvFile), envFile, OwnerOnly);
        else DeleteOwn(dir, EnvFile);
        return BaseArgs(dir, project);
    }

    /// <summary>
    /// Removes the files Dockmaster wrote for a project (compose file, .env, secrets, project files) and the folder when
    /// nothing else is left in it. Everything else stays: `./data` in a compose file is a bind mount into this folder,
    /// the data of the app, and so is what a person keeps in ./files next to the project files. Returns what was left
    /// behind, or null when the folder is gone.
    /// </summary>
    public string? RemoveProjectFiles(string project)
    {
        var dir = ProjectDir(project);
        if (!Directory.Exists(dir)) return null;
        if (IsLink(dir)) return $"{dir} is a symbolic link, left as it is";
        foreach (var f in new[] { ComposeFile, SecretsOverrideFile, EnvFile }) DeleteOwn(dir, f);
        foreach (var sub in new[] { SecretsDir, FilesDir })
        {
            var path = Path.Combine(dir, sub);
            if (IsLink(path)) continue;
            SyncDir(dir, sub, new Dictionary<string, string>(), Readable, ownsAll: sub == SecretsDir);
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
        }
        var left = Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).Order().ToList();
        if (left.Count == 0) { Directory.Delete(dir); return null; }
        return $"{dir} is kept, it holds data of the containers: {string.Join(", ", left)}";
    }

    /// <summary>Base args for an already prepared project, or null when it was never deployed here.</summary>
    public string[]? ExistingProjectArgs(string project)
    {
        var dir = ProjectDir(project);
        return File.Exists(Path.Combine(dir, ComposeFile)) ? BaseArgs(dir, project) : null;
    }

    /// <summary>The compose file of the last deploy here, as written; null when it was never deployed here.</summary>
    public string? ComposeYaml(string project)
    {
        var path = Path.Combine(ProjectDir(project), ComposeFile);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static string[] BaseArgs(string dir, string project)
    {
        var args = new List<string> { "compose", "-p", project, "-f", Path.Combine(dir, ComposeFile) };
        var overridePath = Path.Combine(dir, SecretsOverrideFile);
        if (File.Exists(overridePath)) args.AddRange(["-f", overridePath]);
        var envPath = Path.Combine(dir, EnvFile);
        if (File.Exists(envPath)) args.AddRange(["--env-file", envPath]);
        return [.. args];
    }

    /// <summary>
    /// Secrets become files in &lt;project&gt;/secrets/ plus an override file that declares them
    /// (secrets: name: file: ...), so compose services only need `secrets: [name]`. Compose (without Swarm)
    /// bind-mounts each file to /run/secrets/name; the files are readable by non-root container users (the bind mount
    /// does not traverse the 0700 parent dir).
    /// </summary>
    private static void WriteSecrets(string projectDir, IReadOnlyDictionary<string, string> secrets)
    {
        var names = SyncDir(projectDir, SecretsDir, secrets, Readable, OwnerOnly | UnixFileMode.UserExecute, ownsAll: true);
        if (names.Count == 0)
        {
            DeleteOwn(projectDir, SecretsOverrideFile);
            return;
        }
        var decl = new JsonObject();
        foreach (var name in names) decl[name] = new JsonObject { ["file"] = Path.Combine(projectDir, SecretsDir, name) };
        // JSON is valid YAML, and it spares us quoting rules
        WriteReplacing(Path.Combine(projectDir, SecretsOverrideFile), new JsonObject { ["secrets"] = decl }.ToJsonString(), null);
    }

    /// <summary>
    /// Makes a folder of the project (secrets/; files/, which a service mounts project files from: `./files/nginx.conf`)
    /// hold the given files, and removes the ones written there before that are gone from the set. Only names in the
    /// folder's manifest (&lt;project&gt;/.dockmaster-&lt;folder&gt;: what was written last time) are ever removed —
    /// ./files was an ordinary folder to bind-mount before project files existed, and may hold a person's own data.
    /// Returns the names written.
    /// </summary>
    /// <param name="ownsAll">the folder never held anything else: without a manifest (an older agent wrote it) all its files count as written here</param>
    private static List<string> SyncDir(string projectDir, string folder, IReadOnlyDictionary<string, string> files, UnixFileMode fileMode,
        UnixFileMode? dirMode = null, bool ownsAll = false)
    {
        var dir = Path.Combine(projectDir, folder);
        var manifest = Path.Combine(projectDir, ManifestPrefix + folder);
        RefuseLink(dir);
        var before = File.Exists(manifest) && !IsLink(manifest) ? File.ReadAllLines(manifest).Where(SafeName).ToList()
            : ownsAll && Directory.Exists(dir) ? Directory.GetFiles(dir).Select(f => Path.GetFileName(f)).ToList()
            : [];
        foreach (var name in before.Where(n => !files.ContainsKey(n))) DeleteOwn(dir, name);
        // names are validated by the server; never write outside the dir
        var names = files.Keys.Where(SafeName).Order(StringComparer.Ordinal).ToList();
        if (names.Count == 0)
        {
            DeleteOwn(projectDir, ManifestPrefix + folder);
            return names;
        }
        Directory.CreateDirectory(dir);
        if (dirMode is { } mode && !OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, mode);
        // listed before they are written: a file that is there must be in the manifest, or it is never removed
        WriteReplacing(manifest, string.Join('\n', before.Union(names)) + "\n", OwnerOnly);
        foreach (var name in names) WriteReplacing(Path.Combine(dir, name), files[name], fileMode);
        WriteReplacing(manifest, string.Join('\n', names) + "\n", OwnerOnly);
        return names;
    }

    private static bool SafeName(string name) => name.Length > 0 && !name.Contains('/') && !name.Contains('\\') && name is not ("." or "..");

    private static bool IsLink(string path) => new FileInfo(path).LinkTarget != null;

    /// <summary>
    /// A container with the project folder mounted read-write could turn one of its folders into a link to anywhere
    /// on the host, where the agent would then write and delete.
    /// </summary>
    private static void RefuseLink(string dir)
    {
        if (IsLink(dir)) throw new InvalidOperationException($"{dir} is a symbolic link — Dockmaster does not write through it; remove the link");
    }

    /// <summary>
    /// Writes a file next to its place and moves it over: a link in its place is replaced, never written through, and
    /// nobody reads half a file. A null <paramref name="mode"/> keeps the default permissions.
    /// </summary>
    private static void WriteReplacing(string path, string content, UnixFileMode? mode)
    {
        var tmp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var opts = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        // created with its permissions (a .env is not readable by others even for a moment), set again past the umask
        if (mode is { } m && !OperatingSystem.IsWindows()) opts.UnixCreateMode = m;
        try
        {
            using (var w = new StreamWriter(tmp, opts)) w.Write(content);
            if (mode != null && !OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, mode.Value);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            File.Delete(tmp);
            throw;
        }
    }

    /// <summary>Removes a file Dockmaster wrote; a link in its place goes itself, what it points to stays.</summary>
    private static void DeleteOwn(string dir, string name)
    {
        var f = new FileInfo(Path.Combine(dir, name));
        if (f.Exists || f.LinkTarget != null) f.Delete();
    }

    internal ProcessStartInfo StartInfo(string exe, IEnumerable<string> args, bool stdin = false)
    {
        var psi = new ProcessStartInfo(exe == "docker" ? _opts.DockerCli : exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // a project's secrets are also ${VARIABLES} of its compose file: compose reads its own environment before the
        // .env, so a secret wins over a line of the same name there
        if (ProjectDirOf(psi.ArgumentList) is { } dir)
            foreach (var (name, value) in SecretVariables(dir)) psi.Environment[name] = value;
        // after the secrets, though they leave these names out: the daemon is never one a project names
        psi.Environment["DOCKER_HOST"] = _opts.DockerSocket;
        psi.Environment["COMPOSE_PROGRESS"] = "plain";
        return psi;
    }

    /// <summary>The project folder of a `docker compose -f <project>/docker-compose.yml …` call; null for anything else.</summary>
    private static string? ProjectDirOf(IList<string> args)
    {
        if (args.Count == 0 || args[0] != "compose") return null;
        var f = args.IndexOf("-f");
        return f >= 0 && f + 1 < args.Count && Path.GetFileName(args[f + 1]) == ComposeFile ? Path.GetDirectoryName(args[f + 1]) : null;
    }

    /// <summary>
    /// The secrets written for a project at its last deploy whose names can be variables, with their values; never one
    /// docker compose reads itself (<see cref="ComposeVariables"/>), such as DOCKER_HOST.
    /// </summary>
    public static IEnumerable<(string Name, string Value)> SecretVariables(string projectDir)
    {
        var dir = Path.Combine(projectDir, SecretsDir);
        if (!Directory.Exists(dir) || IsLink(dir)) yield break;
        var manifest = Path.Combine(projectDir, ManifestPrefix + SecretsDir);
        var names = File.Exists(manifest) && !IsLink(manifest) ? File.ReadAllLines(manifest).Where(SafeName) : Directory.GetFiles(dir).Select(f => Path.GetFileName(f));
        foreach (var name in names.Where(n => VariableNameRegex().IsMatch(n) && !ComposeVariables.IsReserved(n)))
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path) || IsLink(path)) continue;
            string value;
            try { value = File.ReadAllText(path); } catch (IOException) { continue; }
            yield return (name, value);
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")] private static partial Regex VariableNameRegex();

    /// <summary>Runs the docker CLI and streams stdout and stderr lines as they come.</summary>
    public async Task<int> RunAsync(IEnumerable<string> args, Func<string, Task> onLine, CancellationToken ct, string? stdin = null)
    {
        using var p = new Process { StartInfo = StartInfo("docker", args, stdin != null) };
        _log.LogDebug("run: docker {Args}", string.Join(' ', p.StartInfo.ArgumentList));
        p.Start();
        if (stdin != null)
        {
            await p.StandardInput.WriteAsync(stdin);
            p.StandardInput.Close();
        }
        // one line at a time to the callback, whichever stream it came from
        var gate = new SemaphoreSlim(1, 1);
        async Task Pump(StreamReader r)
        {
            while (await r.ReadLineAsync(ct) is { } line)
            {
                if (line.Contains("attribute `version` is obsolete")) continue;
                await gate.WaitAsync(ct);
                try { await onLine(line); } finally { gate.Release(); }
            }
        }
        var pumps = Task.WhenAll(Pump(p.StandardOutput), Pump(p.StandardError));
        await WaitOrKillAsync(p, ct);
        await pumps;
        return p.ExitCode;
    }

    /// <summary>Runs an executable ("docker" = the configured CLI) and returns stdout and stderr separately.</summary>
    public async Task<(int code, string stdout, string stderr)> CaptureAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        using var p = new Process { StartInfo = StartInfo(exe, args) };
        p.Start();
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await WaitOrKillAsync(p, ct);
        return (p.ExitCode, await stdout, await stderr);
    }

    private static async Task WaitOrKillAsync(Process p, CancellationToken ct)
    {
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            throw;
        }
    }

    /// <summary>`docker compose config --format json` → resolved services map.</summary>
    public async Task<JsonObject> ConfigAsync(string[] baseArgs, CancellationToken ct)
    {
        var (code, stdout, stderr) = await CaptureAsync("docker", [.. baseArgs, "config", "--format", "json"], ct);
        if (code != 0) throw new InvalidOperationException("compose config failed:\n" + stderr + stdout);
        var start = stdout.IndexOf('{');
        if (start < 0) throw new InvalidOperationException("compose config produced no JSON:\n" + stderr + stdout);
        return JsonNode.Parse(stdout[start..])?.AsObject() ?? throw new InvalidOperationException("bad compose config JSON");
    }

    /// <summary>The hash compose stores in com.docker.compose.config-hash for a service (null if compose cannot tell).</summary>
    public async Task<string?> ConfigHashAsync(string[] baseArgs, string service, CancellationToken ct)
    {
        var (code, stdout, _) = await CaptureAsync("docker", [.. baseArgs, "config", "--hash", service], ct);
        if (code != 0) return null;
        return Lines(stdout)
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(p => p.Length == 2 && p[0] == service)?[1];
    }

    public async Task<List<string>> ContainerIdsAsync(string[] baseArgs, string service, bool onlyRunning, CancellationToken ct)
    {
        var args = new List<string>(baseArgs) { "ps", "-q" };
        if (!onlyRunning) args.Add("-a");
        args.Add(service);
        var (code, stdout, _) = await CaptureAsync("docker", args, ct);
        return code == 0 ? Lines(stdout) : [];
    }

    private static List<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>`docker login` for each registry. Returns what failed ("docker login X failed"), or null when all went through.</summary>
    public async Task<string?> LoginAllAsync(IEnumerable<RegistryCredential> registries, Func<string, Task> progress, CancellationToken ct)
    {
        foreach (var r in registries)
        {
            await progress($"docker login {r.Server}");
            var code = await RunAsync(["login", r.Server, "-u", r.Username, "--password-stdin"], progress, ct, r.Password);
            if (code != 0) return $"docker login {r.Server} failed";
        }
        return null;
    }
}
