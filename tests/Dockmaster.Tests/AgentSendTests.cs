using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Dockmaster.Protocol;
using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

public class AgentSendTests
{
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    internal static AgentRegistry Registry() => new(NullLogger<AgentRegistry>.Instance, new Lifetime());

    internal static AgentSession Session(int id, Func<ServerMessage, CancellationToken, Task> writer) =>
        new() { AgentId = id, Name = "a" + id, Host = new HostInfo(), Cts = new CancellationTokenSource(), Writer = writer };

    [Fact]
    public async Task A_stalled_stream_does_not_hold_the_caller_past_its_time()
    {
        var registry = Registry();
        // a stream another message holds: the wait for it ends with the caller's token only
        registry.Register(Session(1, (_, ct) => Task.Delay(Timeout.Infinite, ct)));
        var watch = Stopwatch.StartNew();
        var r = await registry.SendAsync(1, new Command { Ping = new Ping() }, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.StartsWith("the agent did not answer within", r.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Empty(registry.Get(1)!.Pending);
    }
}
