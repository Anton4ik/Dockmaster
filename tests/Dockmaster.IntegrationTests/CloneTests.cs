using System.Text.Json;
using Dockmaster.Agent.Docker;
using Dockmaster.Protocol;

namespace Dockmaster.IntegrationTests;

/// <summary>Where the agent makes a container as a copy of another: nothing of its configuration may get lost.</summary>
[Collection("docker")]
public sealed class CloneTests(DockerFixture d)
{
    // what a copy must keep exactly (docker's own bookkeeping — ids, times, state — aside)
    private static readonly string[] ConfigKeys = ["Env", "Cmd", "Entrypoint", "WorkingDir", "User", "Labels", "Healthcheck", "ExposedPorts", "Volumes", "Tty", "OpenStdin", "StopSignal", "StopTimeout", "Hostname", "Domainname"];
    private static readonly string[] HostKeys = ["Binds", "Mounts", "PortBindings", "RestartPolicy", "Memory", "MemorySwap", "MemoryReservation", "NanoCpus", "CpuShares",
        "CapAdd", "CapDrop", "Dns", "ExtraHosts", "LogConfig", "NetworkMode", "Privileged", "ReadonlyRootfs", "SecurityOpt", "ShmSize", "Tmpfs", "Ulimits", "Init", "PidsLimit", "Sysctls"];

    [DockerFact]
    public async Task A_recreated_container_keeps_its_whole_configuration()
    {
        var name = d.Prefix + "-recreate";
        var net = d.Prefix + "-rnet";
        await DockerFixture.Must("network", "create", net);
        var id = await d.Run(name, [
            "-e", "TZ=Europe/Riga", "-e", "A=b c", "--label", "app=x", "-p", "127.0.0.1:18092:80", "-v", d.Prefix + "-rvol:/data", "--tmpfs", "/scratch",
            "-m", "96m", "--cpus", "0.3", "--restart", "unless-stopped", "--cap-add", "NET_ADMIN", "--cap-drop", "MKNOD", "--add-host", "db.local:10.1.2.3",
            "--dns", "9.9.9.9", "--ulimit", "nofile=1024:2048", "--init", "--pids-limit", "100", "--shm-size", "32m", "--hostname", "custom-host",
            "--stop-signal", "SIGINT", "--stop-timeout", "17", "--log-opt", "max-size=1m", "-w", "/tmp", "-u", "nobody",
            "--health-cmd", "true", "--health-interval", "30s", "--health-timeout", "3s", "--health-retries", "4", "--health-start-period", "20s", "--health-start-interval", "1s",
            "--network", net, "--network-alias", "svc-alias"]);
        await DockerFixture.Must("network", "create", net + "2");
        await DockerFixture.Must("network", "connect", "--alias", "second-alias", net + "2", id);
        var before = await DockerFixture.Inspect(id);

        var (ok, message) = await d.Get<ContainerRecreator>().RecreateAsync(new RecreateContainer { ContainerId = id, Force = true }, _ => Task.CompletedTask, default);
        Assert.True(ok, message);
        var after = await DockerFixture.Inspect(name);
        Assert.NotEqual(id, after.GetProperty("Id").GetString());
        Assert.True(after.GetProperty("State").GetProperty("Running").GetBoolean());
        AssertSameConfig(before, after);
        Assert.Equal(1_000_000_000, after.GetProperty("Config").GetProperty("Healthcheck").GetProperty("StartInterval").GetInt64());
        Assert.Equal(["svc-alias"], Aliases(after, net).Where(a => a == "svc-alias"));
        Assert.Contains("second-alias", Aliases(after, net + "2"));
        Assert.NotEqual(0, (await DockerFixture.Cli("inspect", id)).code); // the old one is gone
    }

    [DockerFact]
    public async Task A_rolling_update_keeps_the_compose_configuration()
    {
        var project = d.Prefix + "-roll";
        string Compose(string version) => $$"""
            services:
              web:
                image: {{DockerFixture.Image}}
                command: ["sleep", "3600"]
                environment:
                  VERSION: "{{version}}"
                deploy:
                  replicas: 2
                healthcheck:
                  test: ["CMD", "true"]
                  interval: 30s
                  start_period: 30s
                  start_interval: 1s
                labels:
                  dmit: "{{d.Prefix}}"
                tmpfs: [/scratch]
                ulimits:
                  nofile: {soft: 1024, hard: 2048}
            """;
        var deployer = d.Get<Deployer>();
        var log = new List<string>();
        Task Log(string l) { lock (log) log.Add(l); return Task.CompletedTask; }
        try
        {
            var (ok, message) = await deployer.DeployAsync(new DeployProject { Project = project, ComposeYaml = Compose("1"), HealthTimeoutSeconds = 120 }, Log, default);
            Assert.True(ok, message + "\n" + string.Join('\n', log));
            var first = await Replicas(project);
            Assert.Equal(2, first.Count);

            // a change: the replicas are replaced one by one, each a copy of the container compose made
            (ok, message) = await deployer.DeployAsync(new DeployProject { Project = project, ComposeYaml = Compose("2"), HealthTimeoutSeconds = 120 }, Log, default);
            Assert.True(ok, message + "\n" + string.Join('\n', log));
            Assert.Contains(log, l => l.Contains("rolling update"));
            var second = await Replicas(project);
            Assert.Equal(2, second.Count);
            foreach (var (n, c) in second)
            {
                Assert.DoesNotContain(c.GetProperty("Id").GetString(), first.Values.Select(x => x.GetProperty("Id").GetString()));
                var cfg = c.GetProperty("Config");
                Assert.Contains("VERSION=2", cfg.GetProperty("Env").EnumerateArray().Select(e => e.GetString()));
                var hc = cfg.GetProperty("Healthcheck");
                Assert.Equal(30_000_000_000, hc.GetProperty("Interval").GetInt64());
                Assert.Equal(1_000_000_000, hc.GetProperty("StartInterval").GetInt64());
                Assert.Equal(n[^1..], cfg.GetProperty("Labels").GetProperty(DockerService.NumberLabel).GetString());
                Assert.Equal(d.Prefix, cfg.GetProperty("Labels").GetProperty("dmit").GetString());
                var host = c.GetProperty("HostConfig");
                Assert.True(host.GetProperty("Tmpfs").TryGetProperty("/scratch", out _));
                Assert.Equal(2048, host.GetProperty("Ulimits")[0].GetProperty("Hard").GetInt64());
                // reached under its own name on the project network, like compose names it
                Assert.Contains(n, Aliases(c, project + "_default"));
                Assert.Contains("web", Aliases(c, project + "_default"));
                Assert.Equal("healthy", c.GetProperty("State").GetProperty("Health").GetProperty("Status").GetString());
            }
            d.Snapshot("rolling-log.txt", string.Join('\n', log.Where(l => !l.Contains("Container") && !l.Contains("Network")).Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"\d+(\.\d+)?s\b", "<n>s"))));
        }
        finally
        {
            await deployer.ProjectActionAsync(new ProjectAction { Project = project, Op = ProjectOp.ProjectDown }, _ => Task.CompletedTask, default);
        }
    }

    [DockerFact]
    public async Task An_imported_project_and_one_rebuilt_from_its_containers()
    {
        var project = d.Prefix + "-imp";
        var dir = Path.Combine(DockerFixture.DataDir, project);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "docker-compose.yml"), $$"""
            services:
              app:
                image: {{DockerFixture.Image}}
                command: ["sleep", "3600"]
                environment:
                  GREETING: ${GREETING}
                  FIXED: "1"
                ports: ["127.0.0.1:18093:80"]
                volumes: ["data:/data", "./conf:/conf:ro"]
                restart: unless-stopped
                healthcheck:
                  test: ["CMD", "true"]
                  interval: 20s
                labels: {tier: back}
                deploy:
                  resources:
                    limits: {memory: 64M, cpus: "0.5"}
            volumes:
              data:
            """);
        await File.WriteAllTextAsync(Path.Combine(dir, ".env"), "GREETING=hello there\n");
        Directory.CreateDirectory(Path.Combine(dir, "conf"));
        try
        {
            await DockerFixture.Must("compose", "-p", project, "--project-directory", dir, "-f", Path.Combine(dir, "docker-compose.yml"), "up", "-d");
            var imported = await d.Get<ProjectImporter>().ImportAsync(project, _ => Task.CompletedTask, default);
            Assert.Equal("compose files from the labels", imported.Source);
            Assert.Contains("GREETING: ${GREETING}", imported.Compose);
            Assert.Contains("GREETING=hello there", imported.Env);
            d.Snapshot("import.yml", imported.Compose.Replace(dir, "<dir>"));

            // the last resort, straight from the containers
            var docker = d.Get<DockerService>();
            var id = await DockerFixture.Must("ps", "-q", "--filter", "label=com.docker.compose.project=" + project);
            var c = await docker.InspectAsync(id, default);
            var rebuilt = ComposeReconstructor.Build(project, [(c, await docker.TryInspectImageAsync(c.Image, default))]);
            Assert.Contains($"    image: \"{DockerFixture.Image}\"", rebuilt);
            Assert.Contains("      GREETING: \"hello there\"", rebuilt);
            Assert.DoesNotContain("PATH", rebuilt); // the image's own
            Assert.Contains("\"127.0.0.1:18093:80\"", rebuilt);
            Assert.Contains($"\"{project}_data:/data\"", rebuilt);
            Assert.Contains("restart: unless-stopped", rebuilt);
            Assert.Contains("      interval: 20s", rebuilt);
            Assert.Contains("          memory: 64M", rebuilt);
            Assert.Contains("          cpus: \"0.5\"", rebuilt);
            Assert.Contains("\"tier\": \"back\"", rebuilt);
            d.Snapshot("rebuilt.yml", rebuilt.Replace(dir, "<dir>"));
        }
        finally
        {
            await DockerFixture.Cli("compose", "-p", project, "down", "-v");
        }
    }

    private static async Task<Dictionary<string, JsonElement>> Replicas(string project)
    {
        var ids = (await DockerFixture.Must("ps", "-q", "--filter", "label=com.docker.compose.project=" + project)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var all = new Dictionary<string, JsonElement>();
        foreach (var id in ids)
        {
            var c = await DockerFixture.Inspect(id);
            all[c.GetProperty("Name").GetString()!.TrimStart('/')] = c;
        }
        return all;
    }

    private static string[] Aliases(JsonElement c, string network) =>
        c.GetProperty("NetworkSettings").GetProperty("Networks").TryGetProperty(network, out var ep) && ep.GetProperty("Aliases").ValueKind == JsonValueKind.Array
            ? ep.GetProperty("Aliases").EnumerateArray().Select(a => a.GetString()!).ToArray() : [];

    private static void AssertSameConfig(JsonElement before, JsonElement after)
    {
        foreach (var k in ConfigKeys) Assert.True(Same(before.GetProperty("Config"), after.GetProperty("Config"), k), $"Config.{k}: {Get(before.GetProperty("Config"), k)} → {Get(after.GetProperty("Config"), k)}");
        foreach (var k in HostKeys) Assert.True(Same(before.GetProperty("HostConfig"), after.GetProperty("HostConfig"), k), $"HostConfig.{k}: {Get(before.GetProperty("HostConfig"), k)} → {Get(after.GetProperty("HostConfig"), k)}");
        Assert.Equal(before.GetProperty("Config").GetProperty("Image").GetString(), after.GetProperty("Config").GetProperty("Image").GetString());
        Assert.Equal(Networks(before), Networks(after));
    }

    private static string Get(JsonElement o, string k) => o.TryGetProperty(k, out var v) ? v.GetRawText() : "(none)";
    private static bool Same(JsonElement a, JsonElement b, string k) => Get(a, k) == Get(b, k);
    private static string[] Networks(JsonElement c) => c.GetProperty("NetworkSettings").GetProperty("Networks").EnumerateObject().Select(n => n.Name).Order().ToArray();
}
