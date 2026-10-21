using System.Text.Json;
using Dockmaster.Agent.Docker;

namespace Dockmaster.Tests;

public class EngineApiTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void A_listed_container_has_its_name_labels_and_ports()
    {
        var list = EngineApi.ParseList(Json("""
            [{"Id":"abc123def4567890","Names":["/rateme-backend-rest-1"],"Image":"registry/rest:latest","State":"running",
              "Status":"Up 3 hours (healthy)","Created":1790000000,
              "Labels":{"com.docker.compose.project":"rateme","com.docker.compose.service":"backend-rest"},
              "Ports":[{"IP":"10.77.0.2","PrivatePort":5050,"PublicPort":32768,"Type":"tcp"},{"PrivatePort":80,"Type":"tcp"}]},
             {"Id":"0123456789abcdef","Names":null,"Image":"x","State":"exited","Status":"Exited (0)","Created":1,"Labels":null,"Ports":[]}]
            """));
        Assert.Equal(2, list.Count);
        var c = list[0];
        Assert.Equal("rateme-backend-rest-1", c.Name);
        Assert.Equal(1790000000, c.Created);
        Assert.Equal("rateme", c.Labels["com.docker.compose.project"]);
        Assert.Equal(new EngineApi.Port("10.77.0.2", 5050, 32768, "tcp"), c.Ports[0]);
        Assert.Equal(new EngineApi.Port("", 80, 0, "tcp"), c.Ports[1]);
        Assert.Equal("0123456789ab", list[1].Name); // no name: the short id, as docker shows it
        Assert.Empty(list[1].Labels);
    }

    [Fact]
    public void An_inspect_gives_limits_policy_state_and_images()
    {
        var i = EngineApi.ParseInspect(Json("""
            {"Id":"abc","Image":"sha256:1111","RestartCount":3,
             "Config":{"Image":"registry/rest:latest"},
             "HostConfig":{"NanoCpus":1500000000,"Memory":536870912,"RestartPolicy":{"Name":"on-failure","MaximumRetryCount":5}},
             "State":{"Status":"running","ExitCode":137,"OOMKilled":true,"StartedAt":"2026-10-01T06:00:00.123456789Z","Health":{"Status":"healthy"}}}
            """));
        Assert.Equal(new EngineApi.ContainerSummary(3, 1.5, 536870912, "on-failure", 5, 137, true,
            new DateTimeOffset(2026, 10, 1, 6, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), "healthy", "registry/rest:latest", "sha256:1111"), i);
    }

    [Fact]
    public void A_container_never_started_and_without_policy()
    {
        var i = EngineApi.ParseInspect(Json("""
            {"Image":"sha256:2","HostConfig":{"NanoCpus":0,"Memory":0,"RestartPolicy":{"Name":"","MaximumRetryCount":0}},
             "State":{"ExitCode":0,"OOMKilled":false,"StartedAt":"0001-01-01T00:00:00Z"}}
            """));
        Assert.Equal("no", i.RestartPolicy);
        Assert.Equal(0, i.StartedUnix);
        Assert.Equal("", i.Health);
        Assert.Equal("", i.ImageRef);
    }

    [Fact]
    public void An_image_has_its_id_and_digests()
    {
        var img = EngineApi.ParseImage(Json("""{"Id":"sha256:9","RepoDigests":["registry/rest@sha256:aa"],"Size":1}"""));
        Assert.Equal("sha256:9", img.Id);
        Assert.Equal(["registry/rest@sha256:aa"], img.RepoDigests);
        Assert.Empty(EngineApi.ParseImage(Json("""{"Id":"sha256:9","RepoDigests":null}""")).RepoDigests);
    }

    [Fact]
    public void Stats_on_cgroup_v2()
    {
        var s = EngineApi.ParseStats(Json("""
            {"read":"2026-10-01T06:00:00Z","preread":"0001-01-01T00:00:00Z",
             "cpu_stats":{"cpu_usage":{"total_usage":5000000000,"usage_in_kernelmode":1,"usage_in_usermode":2},"system_cpu_usage":900000000000000,"online_cpus":6,"throttling_data":{}},
             "precpu_stats":{"cpu_usage":{"total_usage":0},"throttling_data":{}},
             "memory_stats":{"usage":209715200,"stats":{"inactive_file":10485760,"anon":1},"limit":12000000000},
             "networks":{"eth0":{"rx_bytes":1000,"tx_bytes":2000},"eth1":{"rx_bytes":10,"tx_bytes":20}}}
            """));
        Assert.Equal(new EngineApi.Stats(5000000000, 900000000000000, 0, 0, 6, 0, 209715200, 10485760, 1010, 2020), s);
    }

    [Fact]
    public void Stats_on_cgroup_v1_with_its_previous_reading()
    {
        var s = EngineApi.ParseStats(Json("""
            {"cpu_stats":{"cpu_usage":{"total_usage":200,"percpu_usage":[100,100]},"system_cpu_usage":2000},
             "precpu_stats":{"cpu_usage":{"total_usage":100},"system_cpu_usage":1000},
             "memory_stats":{"usage":500,"max_usage":800,"stats":{"total_inactive_file":100}}}
            """));
        Assert.Equal(new EngineApi.Stats(200, 2000, 100, 1000, 0, 2, 500, 100, 0, 0, 800), s);
    }

    [Fact]
    public void A_null_number_or_flag_reads_as_zero()
    {
        // a running exec: Docker has no exit code yet
        var exec = JsonSerializer.Deserialize<ContainerExecInspectResponse>("""{"ExitCode":null,"Running":true,"Pid":12}""", EngineApi.Json)!;
        Assert.True(exec.Running);
        Assert.Equal(0, exec.ExitCode);
        var c = JsonSerializer.Deserialize<ContainerInspectResponse>("""{"Id":"a","RestartCount":null,"State":{"Running":null,"ExitCode":3},"HostConfig":{"NanoCpus":null,"RestartPolicy":{"Name":"on-failure","MaximumRetryCount":2}},"Config":{"Labels":{"a":"b"}}}""", EngineApi.Json)!;
        Assert.Equal("a", c.ID);
        Assert.False(c.State.Running);
        Assert.Equal(3, c.State.ExitCode);
        Assert.Equal(0, c.HostConfig.NanoCPUs);
        Assert.Equal("on-failure", DockerService.RestartPolicyName(c.HostConfig.RestartPolicy));
        Assert.Equal("b", c.Config.Labels!["a"]);
    }

    [Fact]
    public void Stats_of_a_stopped_container_are_empty()
    {
        var s = EngineApi.ParseStats(Json("""{"cpu_stats":{"cpu_usage":{"total_usage":0}},"precpu_stats":{"cpu_usage":{"total_usage":0}},"memory_stats":{},"networks":null}"""));
        Assert.Equal(new EngineApi.Stats(0, 0, 0, 0, 0, 0, 0, 0, 0, 0), s);
    }
}
