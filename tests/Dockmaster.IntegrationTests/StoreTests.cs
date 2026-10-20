using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dockmaster.Agent.Docker;
using Dockmaster.Protocol;
using Google.Protobuf;

namespace Dockmaster.IntegrationTests;

[Collection("docker")]
public sealed class StoreTests(DockerFixture d)
{
    [DockerFact]
    public async Task A_terminal_runs_keys_resizes_and_exits()
    {
        var exec = d.Get<ExecStore>();
        var id = await d.Run(d.Prefix + "-term", []);
        var output = new StringBuilder();
        var eof = false;
        Task Send(AgentMessage m)
        {
            lock (output) { output.Append(m.Logs.Text); eof |= m.Logs.Eof; }
            return Task.CompletedTask;
        }
        string Out() { lock (output) return output.ToString(); }
        Task Type(string session, string keys) => exec.InputAsync(new ExecInput { Session = session, Data = ByteString.CopyFromUtf8(keys) });

        var run = exec.RunAsync("term-1", new StartExec { Container = id, Cols = 80, Rows = 24 }, Send, default);
        await Type("term-1", "echo sum-$((40+2))\n");
        await DockerFixture.Until(() => Task.FromResult(Out().Contains("sum-42")), TimeSpan.FromSeconds(20), "echo");
        Assert.DoesNotContain("\u001b]7777;", Out()); // the pid is taken off
        await Type("term-1", "stty size\n");
        await DockerFixture.Until(() => Task.FromResult(Out().Contains("24 80")), TimeSpan.FromSeconds(20), () => "first size; got: " + Out());
        // busybox (alpine) would take COLUMNS / LINES over the terminal's size and never follow a resize
        await Type("term-1", "env | grep -c -E '^(COLUMNS|LINES)=' ; echo env-checked\n");
        await DockerFixture.Until(() => Task.FromResult(Out().Contains("env-checked")), TimeSpan.FromSeconds(20), "env");
        Assert.Contains("\n0\r\n", Out().Replace("\r\r", "\r"));
        await exec.InputAsync(new ExecInput { Session = "term-1", Cols = 100, Rows = 30 });
        await Type("term-1", "stty size\n");
        await DockerFixture.Until(() => Task.FromResult(Out().Contains("30 100")), TimeSpan.FromSeconds(20), () => "new size; got: " + Out());
        await Type("term-1", "exit 4\n");
        Assert.Equal("exited with code 4", await run.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.True(eof);

        // closed from the browser: the shell does not stay behind in the container
        using var cts = new CancellationTokenSource();
        output.Clear();
        var second = exec.RunAsync("term-2", new StartExec { Container = id, Cols = 80, Rows = 24 }, Send, cts.Token);
        await Type("term-2", "echo ready\n");
        await DockerFixture.Until(() => Task.FromResult(Out().Contains("ready")), TimeSpan.FromSeconds(20), "second shell");
        cts.Cancel();
        Assert.Equal("closed", await second.WaitAsync(TimeSpan.FromSeconds(30)));
        var top = "";
        await DockerFixture.Until(async () => (top = await DockerFixture.Must("top", id)).Split('\n').Length == 2, TimeSpan.FromSeconds(20), () => "shell gone; top: " + top);
    }

    [DockerFact]
    public async Task A_containers_end_is_seen_with_its_last_lines()
    {
        var watcher = d.Get<ExitWatcher>();
        using var cts = new CancellationTokenSource();
        await watcher.StartAsync(cts.Token);
        try
        {
            await Task.Delay(1000); // the event stream is open
            var id = await d.Run(d.Prefix + "-dies", [], "sh", "-c", "echo first; echo last-words >&2; exit 3");
            await DockerFixture.Until(() => Task.FromResult(watcher.Last(id) is { LogTail.Length: > 0 }), TimeSpan.FromSeconds(30), "exit with log tail");
            var exit = watcher.Last(id)!;
            Assert.Equal(3, exit.ExitCode);
            Assert.False(exit.Oom);
            Assert.Contains("first", exit.LogTail);
            Assert.Contains("last-words", exit.LogTail);
            Assert.True(exit.UnixMs > DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());
        }
        finally
        {
            cts.Cancel();
            await watcher.StopAsync(default);
        }
    }

    [DockerFact]
    public async Task Volumes_create_list_feed_read_and_remove()
    {
        var volumes = d.Get<VolumeStore>();
        var vol = d.Prefix + "-data";
        Assert.Equal($"volume {vol} created", await volumes.CreateAsync(new CreateVolume { Name = vol, Labels = { ["owner"] = "it" } }, _ => Task.CompletedTask, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => volumes.CreateAsync(new CreateVolume { Name = vol }, _ => Task.CompletedTask, default));
        await Assert.ThrowsAsync<ArgumentException>(() => volumes.CreateAsync(new CreateVolume { Name = "no/slash" }, _ => Task.CompletedTask, default));

        // 3 MB in through stdin, back out through stdout
        var payload = new byte[3 << 20];
        new Random(7).NextBytes(payload);
        var input = Channel.CreateUnbounded<DataChunk>();
        for (var i = 0; i < payload.Length; i += 64 << 10)
            await input.Writer.WriteAsync(new DataChunk { Data = ByteString.CopyFrom(payload, i, Math.Min(64 << 10, payload.Length - i)) });
        await input.Writer.WriteAsync(new DataChunk { Eof = true });
        var writes = 0;
        var (code, errors) = await volumes.HelperAsync(vol, false, ["sh", "-c", "cat > /v/blob"], input.Reader, () => { writes++; return Task.CompletedTask; }, null, default);
        Assert.Equal(0, code);
        Assert.Equal("", errors);
        Assert.True(writes > 1);
        var back = new MemoryStream();
        (code, _) = await volumes.HelperAsync(vol, true, ["cat", "/v/blob"], null, null, b => { back.Write(b.Span); return Task.CompletedTask; }, default);
        Assert.Equal(0, code);
        Assert.Equal(payload, back.ToArray());
        (code, errors) = await volumes.HelperAsync(vol, true, ["sh", "-c", "echo oops >&2; exit 3"], null, null, null, default);
        Assert.Equal(3, code);
        Assert.Equal("oops", errors);
        var ro = await Record.ExceptionAsync(() => volumes.HelperAsync(d.Prefix + "-missing", true, ["ls"], null, null, null, default));
        Assert.IsType<InvalidOperationException>(ro);
        Assert.Contains("no volume", ro.Message);
        Assert.Equal("", await DockerFixture.Must("ps", "-aq", "--filter", "label=dockmaster.helper"));

        // an image that keeps data in a VOLUME, and a container on the volume
        var image = d.Prefix + "-img:1";
        var cid = await d.Run(d.Prefix + "-voluser", ["-v", vol + ":/data:ro"]);
        await DockerFixture.Must("commit", "--change", "VOLUME /store", cid, image);
        using (var doc = JsonDocument.Parse(await volumes.ListJsonAsync(new ListVolumes { Sizes = true, Images = { image, d.Prefix + "-notpulled:1" } }, default)))
        {
            var v = doc.RootElement.GetProperty("volumes").EnumerateArray().Single(x => x.GetProperty("name").GetString() == vol);
            Assert.Equal("local", v.GetProperty("driver").GetString());
            Assert.Equal("it", v.GetProperty("labels").GetProperty("owner").GetString());
            Assert.True(v.GetProperty("size").GetInt64() >= payload.Length);
            Assert.False(v.GetProperty("anonymous").GetBoolean());
            var user = v.GetProperty("containers").EnumerateArray().Single();
            Assert.Equal(d.Prefix + "-voluser", user.GetProperty("name").GetString());
            Assert.Equal("/data", user.GetProperty("destination").GetString());
            Assert.False(user.GetProperty("rw").GetBoolean());
            Assert.True(user.GetProperty("running").GetBoolean());
            Assert.Equal(["/store"], doc.RootElement.GetProperty("images").GetProperty(image).EnumerateArray().Select(x => x.GetString()));
            Assert.False(doc.RootElement.GetProperty("images").TryGetProperty(d.Prefix + "-notpulled:1", out _));
        }

        var other = d.Prefix + "-spare";
        await volumes.CreateAsync(new CreateVolume { Name = other }, _ => Task.CompletedTask, default);
        using (var doc = JsonDocument.Parse(await volumes.RemoveJsonAsync([vol, other, d.Prefix + "-never"], default)))
        {
            Assert.Equal([other, d.Prefix + "-never"], doc.RootElement.GetProperty("removed").EnumerateArray().Select(x => x.GetString()));
            Assert.StartsWith("used by " + d.Prefix + "-voluser", doc.RootElement.GetProperty("failed").GetProperty(vol).GetString());
        }
        Assert.NotEqual(0, (await DockerFixture.Cli("volume", "inspect", other)).code);
    }

    [DockerFact]
    public async Task Helpers_left_behind_are_removed()
    {
        await d.Run(d.Prefix + "-oldhelper", ["--label", "dockmaster.helper=volume"]);
        await d.Get<VolumeStore>().RemoveLeftoverHelpersAsync(default);
        Assert.Equal("", await DockerFixture.Must("ps", "-aq", "--filter", "name=" + d.Prefix + "-oldhelper"));
    }

    [DockerFact]
    public async Task Networks_create_connect_list_and_remove()
    {
        var networks = d.Get<NetworkStore>();
        var net = d.Prefix + "-net";
        Assert.Equal($"network {net} created", await networks.CreateAsync(new CreateNetwork { Name = net, Internal = true }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => networks.CreateAsync(new CreateNetwork { Name = net }, default));
        var id = await d.Run(d.Prefix + "-netuser", []);

        Assert.Equal($"connected to {net}", await networks.ContainerNetworkAsync(id, net, true, default));
        var fresh = d.Prefix + "-fresh";
        Assert.Equal($"created network {fresh} and connected", await networks.ContainerNetworkAsync(id, fresh, true, default));
        var nets = (await DockerFixture.Inspect(id)).GetProperty("NetworkSettings").GetProperty("Networks");
        Assert.True(nets.TryGetProperty(net, out _));
        Assert.True(nets.TryGetProperty(fresh, out _));
        Assert.Equal("bridge", await DockerFixture.Must("network", "inspect", "-f", "{{.Driver}}", fresh));

        using (var doc = JsonDocument.Parse(await networks.ListJsonAsync(default)))
        {
            var list = doc.RootElement.EnumerateArray().ToList();
            var n = list.Single(x => x.GetProperty("name").GetString() == net);
            Assert.True(n.GetProperty("internal").GetBoolean());
            Assert.Equal("bridge", n.GetProperty("driver").GetString());
            Assert.Equal("local", n.GetProperty("scope").GetString());
            Assert.Equal([d.Prefix + "-netuser"], n.GetProperty("users").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal(1, n.GetProperty("containers").GetInt32());
            Assert.Matches(@"^\d+\.\d+\.\d+\.\d+/\d+$", n.GetProperty("subnet").GetString());
            Assert.False(n.GetProperty("builtin").GetBoolean());
            Assert.True(n.GetProperty("created").GetInt64() > DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds());
            Assert.True(list.Single(x => x.GetProperty("name").GetString() == "bridge").GetProperty("builtin").GetBoolean());
            Assert.DoesNotContain(list, x => x.GetProperty("name").GetString() is "host" or "none");
        }

        Assert.Equal($"disconnected from {fresh}", await networks.ContainerNetworkAsync(id, fresh, false, default));
        using (var doc = JsonDocument.Parse(await networks.RemoveJsonAsync([net, fresh, "bridge", d.Prefix + "-gone"], default)))
        {
            Assert.Equal([fresh, d.Prefix + "-gone"], doc.RootElement.GetProperty("removed").EnumerateArray().Select(x => x.GetString()));
            Assert.StartsWith("used by", doc.RootElement.GetProperty("failed").GetProperty(net).GetString());
            Assert.Equal("docker's own network", doc.RootElement.GetProperty("failed").GetProperty("bridge").GetString());
        }
    }

    [DockerFact]
    public async Task Images_list_remove_and_prune()
    {
        var images = d.Get<ImageStore>();
        var cid = await d.Run(d.Prefix + "-imguser", []);
        var unused = d.Prefix + "-unused:1";
        await DockerFixture.Must("commit", "--change", "LABEL dmit=1", cid, unused);
        var unusedId = await DockerFixture.Must("image", "inspect", "-f", "{{.Id}}", unused);
        var alpineId = await DockerFixture.Must("image", "inspect", "-f", "{{.Id}}", DockerFixture.Image);

        using (var doc = JsonDocument.Parse(await images.ListJsonAsync(default)))
        {
            var list = doc.RootElement.EnumerateArray().ToList();
            var alpine = list.Single(x => x.GetProperty("id").GetString() == alpineId);
            Assert.Contains(DockerFixture.Image, alpine.GetProperty("tags").EnumerateArray().Select(x => x.GetString()));
            Assert.Contains(d.Prefix + "-imguser", alpine.GetProperty("containers").EnumerateArray().Select(x => x.GetString()));
            Assert.True(alpine.GetProperty("size").GetInt64() > 1_000_000);
            Assert.True(alpine.GetProperty("created").GetInt64() > 1_500_000_000);
            Assert.False(alpine.GetProperty("dangling").GetBoolean());
            Assert.Equal(0, list.Single(x => x.GetProperty("id").GetString() == unusedId).GetProperty("containers").GetArrayLength());
        }

        using (var doc = JsonDocument.Parse(await images.RemoveJsonAsync([unusedId, alpineId, "sha256:" + new string('0', 64)], default)))
        {
            Assert.Equal([unusedId], doc.RootElement.GetProperty("removed").EnumerateArray().Select(x => x.GetString()));
            Assert.StartsWith("used by", doc.RootElement.GetProperty("failed").GetProperty(alpineId).GetString());
            Assert.Equal("no such image", doc.RootElement.GetProperty("failed").GetProperty("sha256:" + new string('0', 64)).GetString());
            Assert.True(doc.RootElement.GetProperty("freed").GetInt64() > 0);
        }
        Assert.NotEqual(0, (await DockerFixture.Cli("image", "inspect", unused)).code);

        // a dangling image (its tag moved on) goes with a prune
        var dangling = d.Prefix + "-dangle:1";
        await DockerFixture.Must("commit", "--change", "LABEL v=1", cid, dangling);
        var oldId = await DockerFixture.Must("image", "inspect", "-f", "{{.Id}}", dangling);
        await DockerFixture.Must("commit", "--change", "LABEL v=2", cid, dangling);
        var pruned = await images.PruneAsync(false, default);
        Assert.StartsWith("freed ", pruned);
        Assert.NotEqual(0, (await DockerFixture.Cli("image", "inspect", oldId)).code);
        Assert.Equal("no unused images", await images.PruneAsync(false, default));
    }
}
