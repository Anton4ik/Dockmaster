namespace Dockmaster.Agent.Docker;

// Waiting for new containers to be READY (see the class summary) and reporting how they ended up.
public sealed partial class Deployer
{
    /// <summary>healthy (with healthcheck) or running without restarts for `monitor` (without).</summary>
    private async Task<bool> WaitReadyAsync(List<string> ids, ServicePlan plan, Func<string, Task> log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + plan.Timeout;
        var restartsAtStart = new Dictionary<string, long>();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var pending = new List<string>();
            foreach (var id in ids)
            {
                try
                {
                    var i = await _docker.InspectAsync(id, ct);
                    restartsAtStart.TryAdd(id, i.RestartCount);
                    var name = i.Name.TrimStart('/');
                    var health = i.State.Health?.Status;
                    if (i.State.Status != "running" && !OneShotDone(i)) pending.Add($"{name} {i.State.Status}");
                    else if (health is "starting" or "unhealthy") pending.Add($"{name} {health}");
                }
                catch (Exception ex) { pending.Add($"{id[..12]} {ex.Message}"); }
            }
            if (pending.Count == 0) break;
            if (DateTime.UtcNow > deadline)
            {
                foreach (var p in pending) await log("  ✗ " + p);
                return false;
            }
            await Task.Delay(Readiness.Poll, ct);
        }

        // settle: healthchecked services get a short crash-loop check, others must survive the monitor window
        var settle = plan.HasHealthcheck ? Readiness.Settle : plan.Monitor;
        if (settle > TimeSpan.Zero) await Task.Delay(settle, ct);
        foreach (var id in ids)
        {
            try
            {
                var i = await _docker.InspectAsync(id, ct);
                var health = i.State.Health?.Status;
                if (i.RestartCount != restartsAtStart.GetValueOrDefault(id) || (i.State.Status != "running" && !OneShotDone(i)) || health == "unhealthy")
                {
                    await log($"  ✗ {i.Name.TrimStart('/')} restarted or died within {settle.TotalSeconds:0}s: {i.State.Status} {health}");
                    return false;
                }
            }
            catch { return false; }
        }
        return true;
    }

    /// <summary>A job container (no restart policy) that finished successfully counts as ready.</summary>
    private static bool OneShotDone(ContainerInspectResponse i) =>
        i.State.Status == "exited" && i.State.ExitCode == 0 && DockerService.RestartPolicyName(i.HostConfig.RestartPolicy) == "no";

    private async Task PrintStatusAsync(List<string> ids, Func<string, Task> log, CancellationToken ct)
    {
        foreach (var id in ids)
        {
            try
            {
                var i = await _docker.InspectAsync(id, ct);
                var h = i.State.Health?.Status is { } hs ? $" ({hs})" : "";
                await log($"  ✓ {i.Name.TrimStart('/')}  {i.State.Status}{h}");
            }
            catch { }
        }
    }

    private async Task<string> SafeNameAsync(string id, CancellationToken ct)
    {
        try { return (await _docker.InspectAsync(id, ct)).Name.TrimStart('/'); } catch { return id[..12]; }
    }
}
