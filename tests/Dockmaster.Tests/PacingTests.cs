using System.Diagnostics;
using Dockmaster.Agent;
using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

public class PacingTests
{
    [Fact]
    public async Task A_shorter_pace_ends_the_wait_going_on()
    {
        var pacing = new Pacing(TimeSpan.FromSeconds(30));
        var watch = Stopwatch.StartNew();
        var wait = pacing.WaitAsync(CancellationToken.None);
        await Task.Delay(50);
        pacing.Set(TimeSpan.FromSeconds(5));
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(5), pacing.Every);
    }

    [Fact]
    public async Task A_longer_pace_waits_for_the_wait_going_on()
    {
        var pacing = new Pacing(TimeSpan.FromMilliseconds(200));
        var wait = pacing.WaitAsync(CancellationToken.None);
        pacing.Set(TimeSpan.FromSeconds(30));
        await wait.WaitAsync(TimeSpan.FromSeconds(5)); // ends after the 200 ms it started with
        Assert.Equal(TimeSpan.FromSeconds(30), pacing.Every);
    }

    [Fact]
    public async Task Cancelling_the_caller_throws()
    {
        var pacing = new Pacing(TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pacing.WaitAsync(cts.Token));
    }

    [Fact]
    public void Watchers_count_each_browser_once_per_server()
    {
        var w = new Watchers();
        w.Connected("a");
        w.Connected("b");
        w.Watch("a", 1);
        w.Watch("a", 1); // the same page twice
        w.Watch("b", 1);
        Assert.Equal(2, w.Connections);
        Assert.True(w.IsWatched(1));
        w.Unwatch("a", 1);
        Assert.True(w.IsWatched(1));
        w.Disconnected("b"); // a closed tab lets go of what it watched
        Assert.False(w.IsWatched(1));
        Assert.Equal(1, w.Connections);
        w.Watch("gone", 2); // a connection the hub never saw
        Assert.False(w.IsWatched(2));
    }

    [Fact]
    public void A_tab_in_the_background_is_connected_but_not_visible()
    {
        var w = new Watchers();
        var changes = 0;
        w.Changed += () => changes++;
        w.Connected("a");
        w.Connected("b");
        Assert.Equal(2, w.Visible);
        w.SetHidden("a", true);
        w.SetHidden("a", true); // said twice: one change
        Assert.Equal(1, w.Visible);
        Assert.Equal(2, w.Connections);
        w.Disconnected("a"); // a hidden tab that closes is not counted twice
        Assert.Equal(1, w.Visible);
        w.SetHidden("b", true);
        Assert.Equal(0, w.Visible);
        w.SetHidden("b", false);
        Assert.Equal(1, w.Visible);
        w.SetHidden("gone", true); // a connection the hub never saw
        Assert.Equal(1, w.Visible);
        Assert.Equal(6, changes); // 2 connects, hide a, disconnect a, hide b, show b
    }

    [Fact]
    public void The_network_page_counts_as_open_while_it_polls()
    {
        var w = new Watchers();
        Assert.False(w.MeshWatched);
        w.MeshSeen();
        Assert.True(w.MeshWatched);
    }
}
