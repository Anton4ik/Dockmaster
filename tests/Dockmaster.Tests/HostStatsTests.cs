using Dockmaster.Agent.Docker;

namespace Dockmaster.Tests;

public class HostStatsTests
{
    [Fact]
    public void The_cpu_line_gives_idle_and_total_ticks()
    {
        // user nice system idle iowait irq softirq steal guest guest_nice
        var (idle, total) = HostStatsCollector.CpuTicks("cpu  100 2 30 1000 50 1 4 0 0 0");
        Assert.Equal(1050, idle);
        Assert.Equal(1187, total);
    }

    [Fact]
    public void A_short_cpu_line_counts_what_it_has()
    {
        // an old kernel without iowait and the rest
        var (idle, total) = HostStatsCollector.CpuTicks("cpu 10 0 10 80");
        Assert.Equal(80, idle);
        Assert.Equal(100, total);
    }

    [Fact]
    public void Meminfo_gives_total_and_used_in_bytes()
    {
        var (total, used) = HostStatsCollector.MemoryOf(["MemTotal:       16314840 kB", "MemFree:         1000000 kB", "MemAvailable:   12000000 kB", "Buffers:          100 kB"]);
        Assert.Equal(16314840L * 1024, total);
        Assert.Equal(4314840L * 1024, used);
    }

    [Fact]
    public void Meminfo_without_available_counts_everything_as_used()
    {
        var (total, used) = HostStatsCollector.MemoryOf(["MemTotal:       1024 kB"]);
        Assert.Equal(1024L * 1024, total);
        Assert.Equal(total, used);
    }

    [Theory]
    [InlineData("0.52 0.58 0.59 1/1250 12345", 0.52)]
    [InlineData("12345.67 54321.00", 12345.67)]
    [InlineData("7", 7)]
    public void The_first_number_of_a_proc_file(string text, double expected) => Assert.Equal(expected, HostStatsCollector.FirstNumber(text));
}
