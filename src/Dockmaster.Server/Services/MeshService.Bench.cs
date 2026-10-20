using Dockmaster.Protocol;

namespace Dockmaster.Server.Services;

public sealed partial class MeshService
{
    private const int BenchSeconds = 5;
    // the iperf3 run itself plus starting the tool on both ends
    private static readonly TimeSpan BenchTimeout = TimeSpan.FromSeconds(BenchSeconds + 30);
    private static readonly TimeSpan BenchServerStart = TimeSpan.FromMilliseconds(1500);

    /// <summary>iperf3 from one host to another over the mesh; the lines go to the browser.</summary>
    public async Task<(bool ok, string message)> BenchAsync(int fromId, int toId, Action<string> onLine, CancellationToken ct)
    {
        var snap = await SnapshotAsync(ct);
        var from = snap.Agents.FirstOrDefault(a => a.Id == fromId);
        var to = snap.Agents.FirstOrDefault(a => a.Id == toId);
        if (from?.InMesh != true || to?.InMesh != true) return (false, "both servers must be in the mesh");
        if (!snap.Online(fromId) || !snap.Online(toId)) return (false, "both servers must be online");
        var path = snap.Plan.Path(fromId, toId);
        if (path == null) return (false, $"{from.Name} and {to.Name} are not in a common network");
        var via = path.Kind switch
        {
            PathKind.ViaHub => $" via {snap.Agent(path.HubId!.Value).Name}",
            PathKind.Lan => " over the LAN",
            PathKind.None => " (no path!)",
            _ => "",
        };
        var rtt = snap.StatusOf(fromId)?.Peers.FirstOrDefault(p => p.MeshIp == to.MeshIp)?.RttMs;
        onLine($"{from.Name} → {to.Name} ({to.MeshIp}){via}, ping {(rtt is > 0 ? $"{rtt:0.##} ms" : "no reply yet")}");
        // one TCP stream each way; iperf3's sent/received numbers are two ends of the same stream, not two directions
        var results = new List<string>();
        foreach (var reverse in new[] { false, true })
        {
            var label = reverse ? $"{to.Name} → {from.Name}" : $"{from.Name} → {to.Name}";
            var serverSide = _agents.SendAsync(toId, new Command { MeshBench = new MeshBench { Server = true, Ip = to.MeshIp!, Seconds = BenchSeconds } }, BenchTimeout, ct);
            await Task.Delay(BenchServerStart, ct);
            onLine($"{label}: measuring {BenchSeconds}s…");
            var client = await _agents.SendAsync(fromId, new Command { MeshBench = new MeshBench { Server = false, Ip = to.MeshIp!, Seconds = BenchSeconds, Reverse = reverse } }, BenchTimeout, ct);
            var server = await serverSide;
            if (!client.Ok) return (false, AgentRegistry.ErrorText(client) + (server.Ok ? "" : $"; target: {AgentRegistry.ErrorText(server)}"));
            var r = IperfJson.Parse(client.Payload);
            if (r.Error != null) return (false, r.Error);
            var line = $"{label}: {r.ReceiveMbps:0.#} Mbit/s{(r.Retransmits > 0 ? $" ({r.Retransmits} retransmits)" : "")}";
            onLine(line);
            results.Add(line);
        }
        return (true, string.Join(" · ", results));
    }
}
