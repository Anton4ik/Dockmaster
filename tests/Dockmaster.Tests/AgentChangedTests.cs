using Dockmaster.Protocol;

namespace Dockmaster.Tests;

public class AgentChangedTests
{
    [Fact]
    public void A_subscriber_that_throws_keeps_neither_the_stream_nor_the_others_from_the_heartbeat()
    {
        var registry = AgentSendTests.Registry();
        var session = AgentSendTests.Session(1, (_, _) => Task.CompletedTask);
        registry.Register(session);
        var heard = new List<(int, bool)>();
        registry.Changed += (_, _) => throw new InvalidOperationException("broken subscriber");
        registry.Changed += (id, heartbeat) => heard.Add((id, heartbeat));

        registry.OnHeartbeat(session, new Heartbeat());
        registry.Notify(1);

        Assert.Equal([(1, true), (1, false)], heard);
    }
}
