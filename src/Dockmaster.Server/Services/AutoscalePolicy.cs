namespace Dockmaster.Server.Services;

public enum ScaleStep { None, Out, In }

/// <summary>What the autoscaler would do now, and the CPU it is based on.</summary>
/// <param name="Cpu">average CPU of the replicas in % of their limit, as it would be with <c>Desired</c> of them (last minute)</param>
public sealed record ScaleDecision(ScaleStep Step, double Cpu, string Why);

/// <summary>A server as a place for one more replica.</summary>
/// <param name="Reserved">memory the containers there may take: their limit, or what they use when they have none</param>
/// <param name="Cpu">host CPU % over the last minutes</param>
/// <param name="Replicas">replicas of this service there now</param>
/// <param name="MemoryUsed">what the host uses now, containers and everything else</param>
public sealed record HostRoom(int AgentId, string Name, bool Online, long MemoryTotal, long Reserved, double Cpu, int Replicas, long MemoryUsed = 0)
{
    public long Free => MemoryTotal - Reserved;
}

/// <summary>
/// The rules of the autoscaler, apart from what it acts on. The CPU of the replicas against their limit decides: one
/// more replica when it stays above the threshold for <see cref="OutMinutes"/> minutes, one less when it would stay
/// well below it with one less for <see cref="InMinutes"/>. The load is assumed to spread over the replicas: with the
/// replicas at 90 % and 2 of them, 3 would run at 60 %.
/// </summary>
public static class AutoscalePolicy
{
    public const int OutMinutes = 3;
    public const int InMinutes = 15;
    /// <summary>After a change: how long before the next one (a new replica needs a moment to take its share).</summary>
    public const int OutCooldown = 5;
    public const int InCooldown = 10;
    /// <summary>One less only when the rest would stay below this share of the threshold.</summary>
    public const double InBelow = 0.75;
    /// <summary>A server takes a guest replica only when this much of its memory stays free after it.</summary>
    public const double MemoryMargin = 0.10;
    public const double HostCpuBusy = 80;
    /// <summary>A server whose memory or CPU passes this for <see cref="OutMinutes"/> minutes gets its guest replicas taken back.</summary>
    public const double HostMemoryFull = 92, HostCpuFull = 90;

    /// <param name="points">the service's minutes, all servers added up, oldest first</param>
    /// <param name="desired">the replicas it runs (or, recommending, would run) now</param>
    /// <param name="sinceChange">minutes since the last change</param>
    public static ScaleDecision Decide(IReadOnlyList<ServicePoint> points, int min, int max, int threshold, int desired, int sinceChange)
    {
        var recent = points.TakeLast(InMinutes).ToList();
        if (recent.Count == 0 || recent[^1].Replicas <= 0) return new(ScaleStep.None, 0, "not running");
        if (recent.Any(p => p.CpuLimit <= 0)) return new(ScaleStep.None, 0, "needs a CPU limit on the service");
        // % of the limit per replica if there were n of them
        // Cpu is in % of a core (docker: 100 = one core), CpuLimit in cores: Cpu / CpuLimit is % of the limit
        double At(ServicePoint p, int n) => p.Replicas <= 0 ? 0 : p.Cpu / p.CpuLimit * p.Replicas / Math.Max(1, n);
        var now = At(recent[^1], desired);

        var last = recent.TakeLast(OutMinutes).ToList();
        var consecutive = last.Count == OutMinutes && last.Zip(last.Skip(1)).All(x => x.Second.Minute - x.First.Minute == 1);
        if (desired < max && consecutive && last.All(p => At(p, desired) >= threshold))
            return sinceChange < OutCooldown ? new(ScaleStep.None, now, $"busy, waiting {OutCooldown - sinceChange} min after the last change")
                : new(ScaleStep.Out, now, $"CPU at {now:0}% of the limit for {OutMinutes} min");
        if (desired > min && recent.Count == InMinutes && recent.All(p => At(p, desired - 1) < threshold * InBelow))
            return sinceChange < InCooldown ? new(ScaleStep.None, now, $"calm, waiting {InCooldown - sinceChange} min after the last change")
                : new(ScaleStep.In, now, $"CPU at {now:0}% of the limit, {At(recent[^1], desired - 1):0}% with one replica less, for {InMinutes} min");
        return new(ScaleStep.None, now, desired >= max && now >= threshold ? $"CPU at {now:0}% of the limit with all {max} replicas (max)" : $"CPU at {now:0}% of the limit");
    }

    /// <summary>
    /// Where one more replica goes: an online server with room for it (its memory need plus a margin, CPU not busy),
    /// the fewest replicas of the service first — on another server rather than next to the others — then the most
    /// free memory. Null with the reasons when none fits.
    /// </summary>
    public static (HostRoom? Host, string Why) Place(IEnumerable<HostRoom> hosts, long need)
    {
        var reasons = new List<string>();
        var fit = new List<HostRoom>();
        foreach (var h in hosts)
        {
            if (!h.Online) reasons.Add($"{h.Name}: offline");
            else if (h.MemoryTotal <= 0) reasons.Add($"{h.Name}: memory unknown");
            else if (h.Free - need < h.MemoryTotal * MemoryMargin) reasons.Add($"{h.Name}: {HealthWatch.Bytes(Math.Max(0, h.Free))} free, needs {HealthWatch.Bytes(need)} and a margin");
            // what the limits leave free may be taken by things outside the containers: the host itself decides too
            else if (h.MemoryUsed + need > h.MemoryTotal * (1 - MemoryMargin)) reasons.Add($"{h.Name}: {HealthWatch.Bytes(Math.Max(0, h.MemoryTotal - h.MemoryUsed))} of its memory free now, needs {HealthWatch.Bytes(need)} and a margin");
            else if (h.Cpu >= HostCpuBusy) reasons.Add($"{h.Name}: CPU at {h.Cpu:0}%");
            else fit.Add(h);
        }
        var best = fit.OrderBy(h => h.Replicas).ThenByDescending(h => h.Free).FirstOrDefault();
        return best != null ? (best, "") : (null, reasons.Count > 0 ? string.Join("; ", reasons) : "no server to put it on");
    }
}
