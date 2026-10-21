using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dockmaster.Agent;
using Dockmaster.Agent.Docker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Dockmaster.IntegrationTests;

/// <summary>
/// A test against the Docker engine at /var/run/docker.sock. Runs only with DOCKMASTER_IT=1, inside a container
/// that has the docker CLI and the socket mounted (see run.sh): what the agent did is checked with the CLI, which
/// does not share any code with the agent.
/// </summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DOCKMASTER_IT") != "1") Skip = "needs a Docker engine (DOCKMASTER_IT=1)";
    }
}

[CollectionDefinition("docker")]
public sealed class DockerCollection : ICollectionFixture<DockerFixture>;

public sealed partial class DockerFixture : IAsyncLifetime
{
    public const string Image = "alpine:3.20";
    /// <summary>Data dir of the agent under test: mounted at the same path on the host (compose resolves paths there).</summary>
    public const string DataDir = "/var/lib/dm-it";

    /// <summary>Every name the tests make starts with it; everything with it is removed at the end.</summary>
    public string Prefix { get; } = "dmit" + Guid.NewGuid().ToString("N")[..6];
    public IServiceProvider Services { get; }

    public DockerFixture()
    {
        var s = new ServiceCollection();
        s.AddLogging(b => b.AddSimpleConsole().SetMinimumLevel(Environment.GetEnvironmentVariable("DOCKMASTER_IT_LOG") == "debug" ? LogLevel.Debug : LogLevel.Warning));
        s.AddSingleton(Options.Create(new AgentOptions { DataDir = DataDir, ServerUrl = "https://test", Token = "test" }));
        s.AddSingleton<DockerService>();
        s.AddSingleton<ComposeRunner>();
        s.AddSingleton<Deployer>();
        s.AddSingleton<ProjectImporter>();
        s.AddSingleton<ContainerRecreator>();
        s.AddSingleton<ImageStore>();
        s.AddSingleton<NetworkStore>();
        s.AddSingleton<VolumeStore>();
        s.AddSingleton<ExecStore>();
        s.AddSingleton<ExitWatcher>();
        s.AddSingleton<VolumeTransfer>();
        s.AddSingleton<ProjectLocks>();
        s.AddSingleton<ImagePuller>();
        Services = s.BuildServiceProvider();
    }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("DOCKMASTER_IT") != "1") return;
        Directory.CreateDirectory(DataDir);
        await Cli("pull", "-q", Image);
    }

    public async Task DisposeAsync()
    {
        if (Environment.GetEnvironmentVariable("DOCKMASTER_IT") != "1") return;
        var (_, ids, _) = await Cli("ps", "-aq", "--filter", "name=" + Prefix);
        foreach (var id in ids.Split('\n', StringSplitOptions.RemoveEmptyEntries)) await Cli("rm", "-f", id);
        var (_, vols, _) = await Cli("volume", "ls", "-q", "--filter", "name=" + Prefix);
        foreach (var v in vols.Split('\n', StringSplitOptions.RemoveEmptyEntries)) await Cli("volume", "rm", "-f", v);
        var (_, nets, _) = await Cli("network", "ls", "-q", "--filter", "name=" + Prefix);
        foreach (var n in nets.Split('\n', StringSplitOptions.RemoveEmptyEntries)) await Cli("network", "rm", n);
        var (_, imgs, _) = await Cli("images", "-q", "--filter", "reference=" + Prefix + "*");
        foreach (var i in imgs.Split('\n', StringSplitOptions.RemoveEmptyEntries).Distinct()) await Cli("rmi", "-f", i);
    }

    /// <summary>The docker CLI: exit code, stdout, stderr.</summary>
    public static async Task<(int code, string stdout, string stderr)> Cli(params string[] args)
    {
        var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, (await stdout).Trim(), (await stderr).Trim());
    }

    /// <summary>The CLI, failing the test when it fails; its stdout.</summary>
    public static async Task<string> Must(params string[] args)
    {
        var (code, stdout, stderr) = await Cli(args);
        Assert.True(code == 0, $"docker {string.Join(' ', args)}: {stderr}");
        return stdout;
    }

    /// <summary>docker inspect of one object, as the CLI sees it.</summary>
    public static async Task<JsonElement> Inspect(string name, string type = "container")
    {
        using var doc = JsonDocument.Parse(await Must("inspect", "--type", type, name));
        return doc.RootElement[0].Clone();
    }

    /// <summary>A container with the extra `docker run` options, running the command in alpine (default: sleeping); its id.</summary>
    public async Task<string> Run(string name, string[] options, params string[] command)
    {
        var args = new List<string> { "run", "-d", "--name", name, "--label", "dmit=" + Prefix };
        args.AddRange(options);
        args.Add(Image);
        args.AddRange(command.Length > 0 ? command : ["sleep", "3600"]);
        return await Must([.. args]);
    }

    public static Task Until(Func<Task<bool>> condition, TimeSpan timeout, string what) => Until(condition, timeout, () => what);

    public static async Task Until(Func<Task<bool>> condition, TimeSpan timeout, Func<string> what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("timed out waiting for " + what());
            await Task.Delay(250);
        }
    }

    /// <summary>
    /// Writes an output of the agent to DOCKMASTER_IT_OUT (when set), with what changes from run to run (ids, times,
    /// the prefix) masked: the same tests run before and after a change, and the two folders are compared.
    /// </summary>
    public void Snapshot(string name, string text)
    {
        if (Environment.GetEnvironmentVariable("DOCKMASTER_IT_OUT") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        text = text.Replace(Prefix, "<p>");
        text = Hex64().Replace(text, "<id>");
        text = Hex12().Replace(text, "<id12>");
        text = Time().Replace(text, "<time>");
        text = Unix().Replace(text, "$1<unix>");
        File.WriteAllText(Path.Combine(dir, name), text);
    }

    [GeneratedRegex("(sha256:)?[0-9a-f]{64}")] private static partial Regex Hex64();
    [GeneratedRegex(@"\b[0-9a-f]{12}\b")] private static partial Regex Hex12();
    [GeneratedRegex(@"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?(Z|[+-]\d\d:\d\d)")] private static partial Regex Time();
    [GeneratedRegex(@"(""(created|start|end|startedUnix|createdUnix)"":)\d+")] private static partial Regex Unix();
}
