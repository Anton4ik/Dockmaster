using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Dockmaster.Protocol;
using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

public class MetricsHistoryTests
{
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private static MetricsHistory New() => new(new AgentRegistry(NullLogger<AgentRegistry>.Instance, new Lifetime()),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataDir"] = Path.GetTempPath() }).Build(),
        new HostingEnvironment { ContentRootPath = Path.GetTempPath() }, NullLogger<MetricsHistory>.Instance);

    private const long Minute = 29_000_000; // unix minutes, some day in 2025

    private static Heartbeat Beat(long minute, int second, params ContainerInfo[] containers)
    {
        var hb = new Heartbeat { UnixMs = minute * 60_000 + second * 1000, Host = new HostStats { CpuPercent = 50, MemoryUsed = 4L << 30, MemoryTotal = 8L << 30 } };
        hb.Containers.AddRange(containers);
        return hb;
    }

    private static ContainerInfo Replica(string id, double cpu, long memory, long rx, double cpuLimit = 0.5, long memLimit = 1L << 30) => new()
    {
        Id = id, Name = "shop-api-" + id, Project = "shop", Service = "api", State = "running",
        CpuPercent = cpu, CpuLimit = cpuLimit, MemoryUsage = memory, MemoryLimit = memLimit, NetRx = rx,
    };

    [Fact]
    public void A_minute_is_the_average_of_its_heartbeats_with_the_replicas_added_up()
    {
        var m = New();
        m.Add(1, Beat(Minute, 0, Replica("1", 20, 100, 1000), Replica("2", 40, 300, 0)));
        m.Add(1, Beat(Minute, 10, Replica("1", 40, 100, 11_000), Replica("2", 60, 300, 0)));
        m.Close(Minute);
        var p = Assert.Single(Assert.Single(m.Services(_ => true, 0)).Value);
        Assert.Equal((2.0, 80.0, 1.0, 400L, 2L << 30), (p.Replicas, p.Cpu, p.CpuLimit, p.Memory, p.MemoryLimit));
        // 10 000 bytes in 10 s, on the second heartbeat only (the first has nothing to compare with)
        Assert.Equal(500, p.Rx);
        Assert.Equal(50, Assert.Single(m.Host(1, 0)).Cpu);
    }

    [Fact]
    public void A_replica_without_a_limit_makes_the_service_unlimited_and_a_restart_does_not_count_as_traffic()
    {
        var m = New();
        m.Add(1, Beat(Minute, 0, Replica("1", 10, 100, 5000), Replica("2", 10, 100, 0, cpuLimit: 0, memLimit: 0)));
        // counters went back: the container restarted
        m.Add(1, Beat(Minute, 5, Replica("1", 10, 100, 10), Replica("2", 10, 100, 0, cpuLimit: 0, memLimit: 0)));
        m.Close(Minute);
        var p = m.Services(_ => true, 0).Single().Value.Single();
        Assert.Equal((0.0, 0L, 0.0), (p.CpuLimit, p.MemoryLimit, p.Rx));
    }

    [Fact]
    public void A_heartbeat_without_stats_repeats_the_last_reading_instead_of_zeros_and_a_traffic_spike()
    {
        var m = New();
        m.Add(1, Beat(Minute, 0, Replica("1", 20, 400, 1_000_000)));
        // docker did not answer for the stats in time: no memory, no counters
        m.Add(1, Beat(Minute, 5, Replica("1", 0, 0, 0)));
        m.Add(1, Beat(Minute, 10, Replica("1", 20, 400, 1_010_000)));
        m.Close(Minute);
        var p = m.Services(_ => true, 0).Single().Value.Single();
        Assert.Equal((1.0, 20.0, 400L), (p.Replicas, p.Cpu, p.Memory));
        // 10 000 bytes in 10 s, on the last heartbeat only
        Assert.Equal(Math.Round(1000 / 3.0), p.Rx);
        Assert.Equal(400, m.Container(1, "shop-api-1", 0).Single().Memory);
    }

    private static ContainerInfo Peaked(ContainerInfo c, long peak, long started = 1)
    {
        c.MemoryPeak = peak;
        c.StartedUnix = started;
        return c;
    }

    [Fact]
    public void The_peak_of_a_minute_is_what_the_kernel_saw_between_two_readings()
    {
        var m = New();
        // a peak from before the first reading may be hours old: it does not count
        m.Add(1, Beat(Minute, 0, Peaked(Replica("1", 10, 100, 0), 900)));
        m.Add(1, Beat(Minute, 30, Peaked(Replica("1", 10, 120, 0), 900)));
        // the next minute it rose between the readings
        m.Add(1, Beat(Minute + 1, 0, Peaked(Replica("1", 10, 100, 0), 950)));
        m.Add(1, Beat(Minute + 1, 30, Peaked(Replica("1", 10, 100, 0), 950)));
        m.Close(Minute + 1);
        Assert.Equal([120L, 950L], m.Container(1, "shop-api-1", 0).Select(p => p.MemoryPeak));
        Assert.Equal([120L, 950L], m.Services(_ => true, 0).Single().Value.Select(p => p.MemoryPeak));
    }

    [Fact]
    public void A_container_out_of_memory_since_the_last_reading_held_all_its_limit()
    {
        var m = New();
        m.Add(1, Beat(Minute, 0, Peaked(Replica("1", 10, 100, 0, memLimit: 300), 150, started: Minute * 60)));
        // killed and started again in between: the peak of its new run is all from after the last reading
        var again = Peaked(Replica("1", 10, 80, 0, memLimit: 300), 90, started: Minute * 60 + 20);
        again.LastExit = new ContainerExit { UnixMs = (Minute * 60 + 19) * 1000, ExitCode = 137, Oom = true };
        m.Add(1, Beat(Minute, 30, again));
        m.Close(Minute);
        var p = m.Container(1, "shop-api-1", 0).Single();
        Assert.Equal((90L, 300L), (p.Memory, p.MemoryPeak));

        // a restart without running out of memory: the new run's peak
        m.Add(1, Beat(Minute + 1, 0, Peaked(Replica("2", 10, 80, 0), 100, started: Minute * 60)));
        m.Add(1, Beat(Minute + 1, 30, Peaked(Replica("2", 10, 80, 0), 400, started: Minute * 60 + 80)));
        m.Close(Minute + 1);
        Assert.Equal(400, m.Container(1, "shop-api-2", 0).Single().MemoryPeak);
    }

    [Fact]
    public void A_late_heartbeat_goes_into_the_next_minute_and_old_minutes_are_dropped()
    {
        var m = New();
        m.Add(1, Beat(Minute, 0, Replica("1", 10, 100, 0)));
        m.Close(Minute);
        m.Add(1, Beat(Minute, 58, Replica("1", 30, 100, 0)));
        m.Close(Minute + 1);
        Assert.Equal([Minute, Minute + 1], m.Services(_ => true, 0).Single().Value.Select(p => p.Minute));
        m.Close(Minute + 1 + (long)MetricsHistory.Keep.TotalMinutes);
        Assert.Empty(m.Services(_ => true, 0));
    }

    [Fact]
    public void A_service_over_servers_is_summed_minute_by_minute()
    {
        var a = new[] { new ServicePoint(1, 2, 50, 1, 100, 200, 10, 20), new ServicePoint(2, 2, 60, 1, 100, 200, 10, 20) };
        var b = new[] { new ServicePoint(2, 1, 30, 0, 50, 100, 5, 5) };
        var sum = MetricsHistory.Sum([a, b]);
        Assert.Equal(2, sum.Count);
        Assert.Equal(new ServicePoint(2, 3, 90, 0, 150, 300, 15, 25), sum[1]);
    }

    [Fact]
    public void Each_container_has_its_own_minutes_by_name_even_outside_a_project_and_over_a_re_create()
    {
        var m = New();
        var standalone = new ContainerInfo { Id = "x", Name = "portainer", State = "running", CpuPercent = 5, MemoryUsage = 50 };
        m.Add(1, Beat(Minute, 0, Replica("1", 20, 100, 1000), Replica("2", 40, 300, 0), standalone));
        m.Add(1, Beat(Minute, 10, Replica("1", 40, 100, 11_000), Replica("2", 60, 300, 0), standalone));
        // re-created: a new id under the same name
        var again = Replica("9", 10, 200, 0);
        again.Name = "shop-api-1";
        m.Add(1, Beat(Minute + 1, 0, again));
        m.Close(Minute + 1);

        var one = m.Container(1, "shop-api-1", 0);
        Assert.Equal([Minute, Minute + 1], one.Select(p => p.Minute));
        Assert.Equal(new ContainerPoint(Minute, 30, 0.5, 100, 1L << 30, 500, 0, 100), one[0]);
        Assert.Equal(new ContainerPoint(Minute + 1, 10, 0.5, 200, 1L << 30, 0, 0, 200), one[1]);
        Assert.Equal(5, m.Container(1, "portainer", 0).Single().Cpu);
        Assert.Empty(m.Container(2, "portainer", 0));
        // the standalone one is in no service
        Assert.Single(m.Services(_ => true, 0));
    }

    /// <summary>The lowest point of each of 48 hours, from the memory at hour <c>h</c>.</summary>
    private static List<MemoryPoint> Lows(Func<int, long> at) => Enumerable.Range(0, 48).Select(h => new MemoryPoint(Minute + h * 60, at(h), 2L << 30)).ToList();

    [Fact]
    public void Memory_whose_low_point_rises_day_over_day_is_a_leak()
    {
        // 300 MB rising by 5 MB an hour, with a garbage collector saw-tooth on top
        var leak = Lows(h => (300L << 20) + h * (5L << 20) + (h % 3) * (20L << 20));
        Assert.Equal(120L << 20, HealthWatch.Leak(leak));
    }

    [Fact]
    public void The_hours_right_after_a_start_are_not_compared()
    {
        // started in the first hour at 2 MB, then flat at 80 MB: only that start hour makes the day before look low
        var lows = Enumerable.Range(0, 48).Select(h => new MemoryPoint(Minute + h * 60, h == 0 ? 2L << 20 : 80L << 20, 100L << 20)).ToList();
        Assert.NotNull(HealthWatch.Leak(lows));
        Assert.Null(HealthWatch.Leak(lows, Minute + 10));
        // a real leak after the warm-up is still one
        var leak = Enumerable.Range(0, 52).Select(h => new MemoryPoint(Minute + h * 60, (300L << 20) + h * (5L << 20), 2L << 30)).ToList();
        Assert.Equal(120L << 20, HealthWatch.Leak(leak, Minute + 10));
    }

    [Fact]
    public void A_container_keeps_the_lowest_reading_of_each_hour_and_starts_over_on_a_restart()
    {
        var m = New();
        var c = Replica("1", 10, 300, 0);
        c.StartedUnix = (Minute - 60) * 60;
        m.Add(1, Beat(Minute, 0, c));
        c.MemoryUsage = 200;
        m.Add(1, Beat(Minute + 1, 0, c));
        c.MemoryUsage = 250;
        m.Add(1, Beat(Minute + 61, 0, c));
        var lows = m.Containers().Single().Lows;
        Assert.Equal([new MemoryPoint(Minute - Minute % 60, 200, 1L << 30), new MemoryPoint(Minute + 61 - (Minute + 61) % 60, 250, 1L << 30)], lows);
        c.StartedUnix = (Minute + 62) * 60;
        c.MemoryUsage = 100;
        m.Add(1, Beat(Minute + 62, 0, c));
        Assert.Equal([100], m.Containers().Single().Lows.Select(p => p.Memory));
    }

    [Theory]
    [InlineData("flat")]
    [InlineData("small")]
    [InlineData("daily")]
    [InlineData("young")]
    public void Memory_that_levels_off_or_grows_little_or_rises_and_falls_with_the_day_is_not(string shape)
    {
        var lows = shape switch
        {
            "flat" => Lows(h => (500L << 20) + (h % 5) * (10L << 20)),
            // rises every hour, but 20 MB in all
            "small" => Lows(h => (500L << 20) + h * (20L << 20) / 48),
            // rises 100 MB through the evening of each day and falls back at night
            "daily" => Lows(h => (300L << 20) + (h % 24 < 16 ? h % 24 : 24 - h % 24) * (100L << 20) / 16),
            // rising, but less than two days old
            _ => Lows(h => (300L << 20) + h * (5L << 20)).Skip(4).ToList(),
        };
        Assert.Null(HealthWatch.Leak(lows));
    }

    [Theory]
    [InlineData(137, true, 512L << 20, "out of memory (limit 512 MB), exit code 137")]
    [InlineData(137, false, 0L, "exit code 137 — killed (SIGKILL): out of memory, or it did not stop in time")]
    [InlineData(1, false, 0L, "exit code 1 — an error in the app")]
    [InlineData(139, false, 0L, "exit code 139 — crashed (segmentation fault)")]
    [InlineData(130, false, 0L, "exit code 130 — ended by signal 2")]
    [InlineData(42, false, 0L, "exit code 42")]
    public void An_exit_is_explained(int code, bool oom, long limit, string text) => Assert.Equal(text, HealthWatch.ExitReason(code, oom, limit));
}
