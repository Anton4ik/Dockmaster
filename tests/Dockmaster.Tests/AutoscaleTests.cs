using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

public class AutoscaleTests
{
    /// <summary>Minutes of a service with <paramref name="replicas"/> replicas of 1 core each at these % of their limit.</summary>
    private static List<ServicePoint> Load(int replicas, params double[] percent) =>
        percent.Select((p, i) => new ServicePoint(1000 + i, replicas, p * replicas, replicas, 100L << 20, 0, 0, 0)).ToList();

    [Fact]
    public void Three_busy_minutes_start_one_more()
    {
        var d = AutoscalePolicy.Decide(Load(2, 50, 80, 85, 90), min: 2, max: 4, threshold: 70, desired: 2, sinceChange: 60);
        Assert.Equal(ScaleStep.Out, d.Step);
        Assert.Equal(90, d.Cpu);
        // two busy minutes are not enough, nor a dip in between
        Assert.Equal(ScaleStep.None, AutoscalePolicy.Decide(Load(2, 50, 50, 85, 90), 2, 4, 70, 2, 60).Step);
        Assert.Equal(ScaleStep.None, AutoscalePolicy.Decide(Load(2, 85, 60, 90), 2, 4, 70, 2, 60).Step);
    }

    [Fact]
    public void Not_beyond_max_and_not_right_after_a_change()
    {
        var busy = Load(4, 95, 95, 95);
        var atMax = AutoscalePolicy.Decide(busy, 2, 4, 70, 4, 60);
        Assert.Equal(ScaleStep.None, atMax.Step);
        Assert.Contains("max", atMax.Why);
        var waiting = AutoscalePolicy.Decide(Load(2, 95, 95, 95), 2, 4, 70, 2, 2);
        Assert.Equal(ScaleStep.None, waiting.Step);
        Assert.Contains("waiting 3 min", waiting.Why);
    }

    [Fact]
    public void The_load_is_spread_over_the_replicas_it_would_run()
    {
        // 2 replicas at 90 %: recommending, it would already run 3 — at 60 % each that is enough
        var d = AutoscalePolicy.Decide(Load(2, 90, 90, 90), 2, 4, 70, 3, 60);
        Assert.Equal(ScaleStep.None, d.Step);
        Assert.Equal(60, d.Cpu);
        // at 120 % of 3 they would still be too busy: one more
        Assert.Equal(ScaleStep.Out, AutoscalePolicy.Decide(Load(2, 120, 120, 120), 2, 4, 70, 3, 60).Step);
    }

    [Fact]
    public void A_quarter_hour_well_below_with_one_less_removes_one()
    {
        // 3 replicas at 30 %: with 2 they would be at 45 %, below 70 % × 0.75
        var calm = Load(3, Enumerable.Repeat(30.0, 15).ToArray());
        Assert.Equal(ScaleStep.In, AutoscalePolicy.Decide(calm, 2, 4, 70, 3, 60).Step);
        // never below the file's count, not after 14 minutes, not when one less would be too busy
        Assert.Equal(ScaleStep.None, AutoscalePolicy.Decide(Load(2, Enumerable.Repeat(10.0, 15).ToArray()), 2, 4, 70, 2, 60).Step);
        Assert.Equal(ScaleStep.None, AutoscalePolicy.Decide(Load(3, Enumerable.Repeat(30.0, 14).ToArray()), 2, 4, 70, 3, 60).Step);
        Assert.Equal(ScaleStep.None, AutoscalePolicy.Decide(Load(3, Enumerable.Repeat(40.0, 15).ToArray()), 2, 4, 70, 3, 60).Step);
    }

    [Fact]
    public void Without_a_cpu_limit_it_does_nothing()
    {
        var unlimited = Load(2, 95, 95, 95).Select(p => p with { CpuLimit = 0 }).ToList();
        var d = AutoscalePolicy.Decide(unlimited, 2, 4, 70, 2, 60);
        Assert.Equal(ScaleStep.None, d.Step);
        Assert.Contains("CPU limit", d.Why);
    }

    [Fact]
    public void A_replica_goes_where_there_is_room_another_server_first()
    {
        const long gb = 1L << 30;
        var hosts = new[]
        {
            new HostRoom(1, "alpha", true, 16 * gb, 4 * gb, 20, Replicas: 2),
            new HostRoom(2, "beta", true, 8 * gb, 6 * gb, 10, Replicas: 0),
            new HostRoom(3, "gamma", true, 8 * gb, 2 * gb, 10, Replicas: 0),
        };
        Assert.Equal("gamma", AutoscalePolicy.Place(hosts, gb).Host?.Name);
        // beta: 2 GB free, 1 GB needed leaves less than the 10 % margin free? 1 GB left ≥ 0.8 GB: it fits, but gamma has more room
        Assert.Equal("beta", AutoscalePolicy.Place(hosts.Where(h => h.Name != "gamma"), gb).Host?.Name);
        var (none, why) = AutoscalePolicy.Place([hosts[1] with { Reserved = 7 * gb }, hosts[2] with { Online = false }, hosts[0] with { Cpu = 95 }], gb);
        Assert.Null(none);
        Assert.Contains("beta: 1 GB free", why);
        Assert.Contains("gamma: offline", why);
        Assert.Contains("alpha: CPU at 95%", why);
    }

    [Fact]
    public void A_server_whose_memory_is_taken_outside_the_containers_gets_no_replica()
    {
        const long gb = 1L << 30;
        // the limits leave 6 GB, but the host uses 7.5 of its 8 GB already
        var full = new HostRoom(2, "beta", true, 8 * gb, 2 * gb, 10, 0, MemoryUsed: 7 * gb + gb / 2);
        var (host, why) = AutoscalePolicy.Place([full], gb);
        Assert.Null(host);
        Assert.Contains("of its memory free now", why);
    }

    [Fact]
    public void A_new_replica_count_or_autoscaling_leave_the_fingerprint_alone_other_changes_do_not()
    {
        const string yaml = "services:\n  api:\n    image: app:1\n    deploy:\n      replicas: 2\n";
        var f = ComposeServices.Fingerprint(yaml, "api", "A=1");
        Assert.NotNull(f);
        Assert.Equal(f, ComposeServices.Fingerprint(yaml.Replace("replicas: 2", "replicas: 5"), "api", "A=1"));
        Assert.Equal(f, ComposeServices.Fingerprint(ComposeEditor.SetAutoscale(yaml, "api", new AutoscaleSpec(4, 70, [], true)), "api", "A=1"));
        Assert.NotEqual(f, ComposeServices.Fingerprint(yaml.Replace("app:1", "app:2"), "api", "A=1"));
        Assert.NotEqual(f, ComposeServices.Fingerprint(yaml, "api", "A=2"));
        Assert.Null(ComposeServices.Fingerprint(yaml, "nope", ""));
    }

    [Fact]
    public void Autoscale_is_written_into_the_compose_file_and_removed_again()
    {
        const string yaml = "services:\n  api:\n    image: app:1 # the app\n    deploy:\n      replicas: 2\n";
        var set = ComposeEditor.SetAutoscale(yaml, "api", new AutoscaleSpec(4, 75, ["beta"], true));
        Assert.Contains("# the app", set);
        var spec = ComposeServices.Parse(set).Single().Autoscale!;
        Assert.Equal((4, 75, "auto"), (spec.Max, spec.Cpu, spec.Mode));
        Assert.Equal(["beta"], spec.AgentNames);
        var changed = ComposeEditor.SetAutoscale(set, "api", new AutoscaleSpec(3, 70, [], false));
        Assert.Equal((3, "recommend", 0), (ComposeServices.Parse(changed).Single().Autoscale!.Max, ComposeServices.Parse(changed).Single().Autoscale!.Mode,
            ComposeServices.Parse(changed).Single().Autoscale!.AgentNames.Length));
        Assert.Equal(yaml, ComposeEditor.SetAutoscale(changed, "api", null));
    }
}
