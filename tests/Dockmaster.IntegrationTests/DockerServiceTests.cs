using System.Text;
using System.Text.Json;
using Dockmaster.Agent.Docker;
using Dockmaster.Protocol;

namespace Dockmaster.IntegrationTests;

[Collection("docker")]
public sealed class DockerServiceTests(DockerFixture d)
{
    private readonly DockerService _docker = d.Get<DockerService>();

    [DockerFact]
    public async Task Version_and_info_are_the_engines()
    {
        var version = await _docker.VersionAsync(default);
        var info = await _docker.InfoAsync(default);
        Assert.Equal(await DockerFixture.Must("version", "-f", "{{.Server.Version}}"), version.Version);
        Assert.Equal(await DockerFixture.Must("version", "-f", "{{.Server.Arch}}"), version.Arch);
        Assert.Equal(await DockerFixture.Must("info", "-f", "{{.NCPU}}"), info.NCPU.ToString());
        Assert.Equal(await DockerFixture.Must("info", "-f", "{{.MemTotal}}"), info.MemTotal.ToString());
        Assert.Equal(await DockerFixture.Must("info", "-f", "{{.DockerRootDir}}"), info.DockerRootDir);
        Assert.Equal(await DockerFixture.Must("info", "-f", "{{.OperatingSystem}}"), info.OperatingSystem);
        Assert.Equal(await DockerFixture.Must("info", "-f", "{{.CPUCfsQuota}}"), info.CPUCfsQuota ? "true" : "false");
    }

    [DockerFact]
    public async Task The_heartbeat_list_has_state_limits_ports_health_and_stats()
    {
        var name = d.Prefix + "-list";
        var id = await d.Run(name, ["-p", "127.0.0.1:18091:80", "--cpus", "0.5", "-m", "64m", "--restart", "on-failure:3",
            "--health-cmd", "true", "--health-interval", "1s", "--label", "com.docker.compose.project=" + d.Prefix, "--label", "com.docker.compose.service=web"]);
        await DockerFixture.Until(async () => (await DockerFixture.Must("inspect", "-f", "{{.State.Health.Status}}", id)) == "healthy", TimeSpan.FromSeconds(30), "healthy");
        await _docker.ListContainersAsync(default); // the first reading of the CPU counters
        await Task.Delay(1500);
        var c = (await _docker.ListContainersAsync(default)).Single(x => x.Name == name);

        Assert.Equal(id, c.Id);
        Assert.Equal(DockerFixture.Image, c.Image);
        Assert.Equal("running", c.State);
        Assert.StartsWith("Up ", c.Status);
        Assert.Equal(d.Prefix, c.Project);
        Assert.Equal("web", c.Service);
        Assert.Equal(["127.0.0.1:18091->80/tcp"], c.Ports);
        Assert.Equal("healthy", c.Health);
        Assert.Equal(0.5, c.CpuLimit);
        Assert.Equal(64L << 20, c.MemoryLimit);
        Assert.Equal("on-failure", c.RestartPolicy);
        Assert.Equal(3, c.RestartMax);
        Assert.Equal(DockerFixture.Image, c.ImageRef);
        Assert.NotEmpty(c.RepoDigests);
        Assert.False(c.NewerImageLocal);
        Assert.True(c.StartedUnix > DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds());
        Assert.True(c.CreatedUnix > DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds());
        Assert.True(c.MemoryUsage > 0);
        Assert.True(c.CpuPercent >= 0);

        // a stopped container: its exit code, no stats
        await DockerFixture.Must("rm", "-f", id);
        var exited = d.Prefix + "-exited";
        var eid = await d.Run(exited, [], "sh", "-c", "exit 7");
        await DockerFixture.Until(async () => (await DockerFixture.Must("inspect", "-f", "{{.State.Status}}", eid)) == "exited", TimeSpan.FromSeconds(20), "exit");
        var e = (await _docker.ListContainersAsync(default)).Single(x => x.Name == exited);
        Assert.Equal("exited", e.State);
        Assert.Equal(7, e.ExitCode);
        Assert.Equal(0, e.MemoryUsage);
    }

    [DockerFact]
    public async Task A_memory_spike_between_two_heartbeats_is_in_its_peak()
    {
        var name = d.Prefix + "-peak";
        // 40 MB held for a moment (dd's buffer), long before the heartbeat reads its stats
        var id = await d.Run(name, ["-m", "64m"], "sh", "-c", "dd if=/dev/zero of=/dev/null bs=40M count=1 2>/dev/null; touch /done; sleep 3600");
        await DockerFixture.Until(async () => (await DockerFixture.Cli("exec", id, "test", "-e", "/done")).code == 0, TimeSpan.FromSeconds(20), "the spike");
        var c = (await _docker.ListContainersAsync(default)).Single(x => x.Name == name);
        Assert.True(c.MemoryPeak >= 40L << 20, $"peak {c.MemoryPeak}");
        Assert.True(c.MemoryUsage < 20L << 20, $"usage {c.MemoryUsage}");
    }

    [DockerFact]
    public async Task Inspect_and_the_container_page()
    {
        var name = d.Prefix + "-insp";
        var id = await d.Run(name, ["-e", "TZ=Europe/Riga", "-m", "32m", "--label", "x=y", "-v", d.Prefix + "-vol:/data", "--health-cmd", "true"]);
        var i = await _docker.InspectAsync(name, default);
        Assert.Equal(id, i.ID);
        Assert.Equal("/" + name, i.Name);
        Assert.True(i.State.Running);
        Assert.Equal("y", i.Config.Labels!["x"]);
        Assert.Contains("TZ=Europe/Riga", i.Config.Env!);
        Assert.Equal(32L << 20, i.HostConfig.Memory);
        Assert.Equal("no", DockerService.RestartPolicyName(i.HostConfig.RestartPolicy));
        Assert.Contains(i.Mounts!, m => m.Destination == "/data" && m.Name == d.Prefix + "-vol" && m.RW);

        var json = await _docker.InspectJsonAsync(name, default);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        Assert.Equal(name, r.GetProperty("name").GetString());
        Assert.Equal(DockerFixture.Image, r.GetProperty("image").GetString());
        Assert.Equal("running", r.GetProperty("state").GetProperty("status").GetString());
        Assert.Equal(32L << 20, r.GetProperty("memoryLimit").GetInt64());
        Assert.Contains(r.GetProperty("env").EnumerateArray(), e => e.GetProperty("name").GetString() == "TZ" && !e.GetProperty("fromImage").GetBoolean());
        Assert.Contains(r.GetProperty("env").EnumerateArray(), e => e.GetProperty("name").GetString() == "PATH" && e.GetProperty("fromImage").GetBoolean());
        Assert.Equal(["true"], r.GetProperty("healthcheck").GetProperty("test").EnumerateArray().Select(x => x.GetString()).Skip(1));
        d.Snapshot("container-page.json", Pretty(json));

        var missing = await Record.ExceptionAsync(() => _docker.InspectAsync(d.Prefix + "-nope", default));
        Assert.NotNull(missing);
        Assert.Contains("No such container", missing.Message);
    }

    [DockerFact]
    public async Task Actions_stop_start_restart_kill_remove()
    {
        var name = d.Prefix + "-act";
        var id = await d.Run(name, []);
        Task<string> State() => DockerFixture.Must("inspect", "-f", "{{.State.Status}}", id);

        await _docker.ContainerActionAsync(id, ContainerOp.ContainerStop, default);
        Assert.Equal("exited", await State());
        Assert.True(await _docker.StartAsync(id, default));
        Assert.Equal("running", await State());
        Assert.False(await _docker.StartAsync(id, default)); // running already
        var started = await DockerFixture.Must("inspect", "-f", "{{.State.StartedAt}}", id);
        await _docker.ContainerActionAsync(id, ContainerOp.ContainerRestart, default);
        Assert.NotEqual(started, await DockerFixture.Must("inspect", "-f", "{{.State.StartedAt}}", id));
        await _docker.ContainerActionAsync(id, ContainerOp.ContainerKill, default);
        Assert.Equal("exited", await State());
        await _docker.ContainerActionAsync(id, ContainerOp.ContainerStart, default);
        Assert.Equal("running", await State());
        await _docker.RenameAsync(id, name + "-renamed", default);
        Assert.Equal("/" + name + "-renamed", await DockerFixture.Must("inspect", "-f", "{{.Name}}", id));
        await _docker.ContainerActionAsync(id, ContainerOp.ContainerRemove, default);
        Assert.NotEqual(0, (await DockerFixture.Cli("inspect", id)).code);
        await _docker.TryRemoveAsync(id, default); // gone: no error
    }

    [DockerFact]
    public async Task Limits_and_restart_policy_change_live()
    {
        var id = await d.Run(d.Prefix + "-upd", ["-m", "64m"]);
        await _docker.UpdateResourcesAsync(id, 128L << 20, 0.25, default);
        var h = (await DockerFixture.Inspect(id)).GetProperty("HostConfig");
        Assert.Equal(128L << 20, h.GetProperty("Memory").GetInt64());
        Assert.Equal(192L << 20, h.GetProperty("MemorySwap").GetInt64()); // docker run -m 64m gave swap 128m: the 64m on top stays
        Assert.Equal(250_000_000, h.GetProperty("NanoCpus").GetInt64());

        Assert.Equal("restart policy set to on-failure (max 4)", await _docker.UpdateRestartPolicyAsync(id, "on-failure", 4, default));
        h = (await DockerFixture.Inspect(id)).GetProperty("HostConfig");
        Assert.Equal("on-failure", h.GetProperty("RestartPolicy").GetProperty("Name").GetString());
        Assert.Equal(4, h.GetProperty("RestartPolicy").GetProperty("MaximumRetryCount").GetInt32());
        await _docker.UpdateRestartPolicyAsync(id, "unless-stopped", 9, default);
        h = (await DockerFixture.Inspect(id)).GetProperty("HostConfig");
        Assert.Equal("unless-stopped", h.GetProperty("RestartPolicy").GetProperty("Name").GetString());
        Assert.Equal(0, h.GetProperty("RestartPolicy").GetProperty("MaximumRetryCount").GetInt32());
        await Assert.ThrowsAsync<ArgumentException>(() => _docker.UpdateRestartPolicyAsync(id, "sometimes", 0, default));
    }

    [DockerFact]
    public async Task Logs_of_stdout_and_stderr_tail_and_follow()
    {
        var id = await d.Run(d.Prefix + "-logs", [], "sh", "-c", "for i in 1 2 3 4 5; do echo out$i; echo err$i >&2; done; echo ünïcødé; sleep 3600");
        await DockerFixture.Until(async () => (await DockerFixture.Must("logs", id)).Contains("ünïcødé"), TimeSpan.FromSeconds(20), "logs");

        var all = new StringBuilder();
        await _docker.StreamLogsAsync(id, 0, false, null, t => { all.Append(t); return Task.CompletedTask; }, default);
        var lines = all.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(11, lines.Length);
        Assert.All(lines, l => Assert.Matches(@"^\d{4}-\d\d-\d\dT\S+ \S+$", l)); // with timestamps
        Assert.Contains(lines, l => l.EndsWith(" err3"));
        Assert.Contains(lines, l => l.EndsWith(" ünïcødé")); // stdout and stderr each in order, not with each other

        var tail = new StringBuilder();
        await _docker.StreamLogsAsync(id, 2, false, null, t => { tail.Append(t); return Task.CompletedTask; }, default);
        Assert.Equal(2, tail.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

        // follow: what is written later comes in, until cancelled
        var followed = new StringBuilder();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var follow = _docker.StreamLogsAsync(id, 1, true, null, t => { lock (followed) followed.Append(t); return Task.CompletedTask; }, cts.Token);
        await DockerFixture.Must("exec", id, "sh", "-c", "echo later > /proc/1/fd/1");
        await DockerFixture.Until(() => Task.FromResult(followed.ToString().Contains("later")), TimeSpan.FromSeconds(20), "followed line");
        cts.Cancel();
        try { await follow; } catch (OperationCanceledException) { }
    }

    [DockerFact]
    public async Task Logs_of_a_tty_container()
    {
        var id = await d.Run(d.Prefix + "-ttylogs", ["-t"], "sh", "-c", "echo hello-tty; sleep 3600");
        await DockerFixture.Until(async () => (await DockerFixture.Must("logs", id)).Contains("hello-tty"), TimeSpan.FromSeconds(20), "logs");
        var all = new StringBuilder();
        await _docker.StreamLogsAsync(id, 0, false, null, t => { all.Append(t); return Task.CompletedTask; }, default);
        Assert.Contains(" hello-tty", all.ToString());
    }

    [DockerFact]
    public async Task Exec_returns_exit_code_and_both_outputs()
    {
        var id = await d.Run(d.Prefix + "-exec", []);
        var (code, output) = await _docker.ExecAsync(id, "echo to-out; echo to-err >&2; exit 5", TimeSpan.FromSeconds(20), default);
        Assert.Equal(5, code);
        Assert.Contains("to-out", output);
        Assert.Contains("to-err", output);
        var big = await _docker.ExecAsync(id, "head -c 300000 /dev/zero | tr '\\0' x", TimeSpan.FromSeconds(20), default);
        Assert.Equal(0, big.exitCode);
        Assert.Equal(300000, big.output.Length);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _docker.ExecAsync(id, "sleep 30", TimeSpan.FromSeconds(1), default));
    }

    [DockerFact]
    public async Task The_agents_own_container()
    {
        // the tests run in a container like the agent: its own image, and the data dir mounted at the same path
        Assert.True(DockerService.InContainer);
        var image = await _docker.OwnImageAsync(default);
        Assert.Equal(await DockerFixture.Must("inspect", "-f", "{{.Image}}", DockerService.SelfId), image);
        Assert.Null(await _docker.DataDirProblemAsync(DockerFixture.DataDir, default));
        Assert.Contains("is not mounted from the host", await _docker.DataDirProblemAsync("/nowhere", default));
    }

    private static string Pretty(string json) => JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement, new JsonSerializerOptions { WriteIndented = true });
}
