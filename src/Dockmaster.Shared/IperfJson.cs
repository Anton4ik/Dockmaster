using System.Text.Json.Nodes;

namespace Dockmaster.Protocol;

/// <summary>The numbers of an `iperf3 -J` client run (the mesh benchmark), read on both sides of the wire.</summary>
public static class IperfJson
{
    public sealed record Result(double SendMbps, double ReceiveMbps, long Retransmits, string? Error);

    public static Result Parse(string json)
    {
        try
        {
            var o = JsonNode.Parse(json)?.AsObject();
            if (o == null) return new Result(0, 0, 0, "no output");
            if (o["error"] is { } err) return new Result(0, 0, 0, err.GetValue<string>());
            var end = o["end"]?.AsObject();
            var sent = end?["sum_sent"]?.AsObject();
            var recv = end?["sum_received"]?.AsObject();
            return new Result(
                Math.Round((sent?["bits_per_second"]?.GetValue<double>() ?? 0) / 1e6, 1),
                Math.Round((recv?["bits_per_second"]?.GetValue<double>() ?? 0) / 1e6, 1),
                sent?["retransmits"]?.GetValue<long>() ?? 0,
                null);
        }
        catch (Exception ex) { return new Result(0, 0, 0, "unreadable iperf3 output: " + ex.Message); }
    }
}
