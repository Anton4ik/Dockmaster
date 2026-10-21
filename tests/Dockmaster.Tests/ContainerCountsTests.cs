using Dockmaster.Protocol;
using Dockmaster.Server.Api;
using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

public class ContainerCountsTests
{
    private static ContainerInfo C(string name, string state, string service = "", long memoryLimit = 0, double cpuLimit = 0, int restarts = 0, int exitCode = 0) =>
        new() { Id = name, Name = name, State = state, Project = service.Length > 0 ? "p" : "", Service = service, MemoryLimit = memoryLimit, CpuLimit = cpuLimit, RestartCount = restarts, ExitCode = exitCode };

    [Fact]
    public void Running_total_problems_and_limits_come_from_one_pass()
    {
        var counts = Mappers.Counts(
            [C("web-1", "running", "web", 512, 0.5), C("web-2", "running", "web", 512, 0.5), C("db", "exited", "db", 1024, 1, restarts: 2), C("tmp", "created")],
            _ => false);
        Assert.Equal((2, 4, 1, 0, 1024L, 1.0), counts);
    }

    [Fact]
    public void A_job_waiting_for_its_run_is_neither_running_nor_stopped()
    {
        // the backup job finished (exit 0): counted apart, not as a stopped container; the failed one is a problem
        var counts = Mappers.Counts(
            [C("web", "running", "web"), C("backup", "exited", "backup"), C("report", "exited", "report", exitCode: 1)],
            c => c.Service is "backup" or "report");
        Assert.Equal(1, counts.Running);
        Assert.Equal(1, counts.Total);
        Assert.Equal(1, counts.Problems);
        Assert.Equal(2, counts.Jobs);
    }

    [Fact]
    public void A_container_stopped_by_hand_after_restarts_is_not_a_problem()
    {
        // unless-stopped and exited: Docker would have restarted it, so someone stopped it — the old restarts are history
        var stopped = C("worker", "exited", "worker", restarts: 34);
        stopped.RestartPolicy = "unless-stopped";
        var gaveUp = C("api", "exited", "api", restarts: 5, exitCode: 1);
        gaveUp.RestartPolicy = "on-failure";
        var counts = Mappers.Counts([stopped, gaveUp], _ => false);
        Assert.Equal(1, counts.Problems);
    }

    [Fact]
    public void Containers_of_several_servers_are_counted_with_their_server()
    {
        // no jobs known: every container counts as a service's
        var jobs = new JobIndex(null!);
        var counts = Mappers.Counts([(1, C("web-1", "running", "web", 256, 1)), (2, C("web-2", "running", "web", 256, 1)), (2, C("db", "exited", "db"))], jobs);
        Assert.Equal((2, 3, 0, 0, 512L, 2.0), counts);
    }
}
