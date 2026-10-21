using System.Text.Json.Nodes;

namespace Dockmaster.Agent.Docker;

// Rolling updates: new replicas next to the old ones, batch by batch, each taking over its predecessor's slot.
public sealed partial class Deployer
{
    private static readonly TimeSpan HookTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DrainPoll = TimeSpan.FromSeconds(1);

    private sealed record SlotSwap(string NewId, string? OldId, string OldName, int Slot);

    private async Task<bool> RolloutAsync(string[] baseArgs, string project, ServicePlan plan, string? image, bool force, Func<string, Task> log, CancellationToken ct)
    {
        await TidyAsync(baseArgs, project, plan.Name, log, ct);
        var old = await OrderedByCreationAsync(await _compose.ContainerIdsAsync(baseArgs, plan.Name, true, ct), ct);
        if (old.Count == 0)
        {
            await log("  no running replicas → plain start");
            return await RecreateAsync(baseArgs, plan, false, log, ct);
        }
        if (!force && await UpToDateAsync(baseArgs, plan, image, old, ct))
        {
            await log($"  all {old.Count} replicas already run the current image and config — nothing to replace");
            return true;
        }
        return await StartReplicasAsync(baseArgs, project, plan, old, true, log, ct);
    }

    /// <summary>
    /// Only the replica count changed (every running replica has the current image and config): new replicas start
    /// next to the running ones, or the ones in the highest slots are drained and removed; the others are not touched.
    /// Null when that is not the case: the service then goes through its update strategy.
    /// </summary>
    private async Task<bool?> ScaleAsync(string[] baseArgs, string project, ServicePlan plan, string? image, Func<string, Task> log, CancellationToken ct)
    {
        await TidyAsync(baseArgs, project, plan.Name, log, ct);
        var running = await OrderedByCreationAsync(await _compose.ContainerIdsAsync(baseArgs, plan.Name, true, ct), ct);
        if (running.Count == 0 || running.Count == plan.Replicas || !await RunsCurrentAsync(baseArgs, plan, image, running, ct)) return null;
        if (running.Count < plan.Replicas)
        {
            await log($"  {running.Count} replica(s) run the current image and config — adding {plan.Replicas - running.Count}, the running ones stay");
            return await StartReplicasAsync(baseArgs, project, plan, running, false, log, ct);
        }
        var slotted = new List<(string Id, int Slot)>();
        foreach (var id in running) slotted.Add((id, await SlotAsync(id, project, plan.Name, ct)));
        var extra = slotted.OrderByDescending(x => x.Slot).Take(running.Count - plan.Replicas).Select(x => x.Id).ToList();
        await log($"  {running.Count} replica(s) run the current image and config — removing {extra.Count}, the others stay");
        var retired = true;
        foreach (var id in extra) retired &= await RetireAsync(id, plan, log, ct);
        return await SettleAsync(baseArgs, plan, log, ct) && retired;
    }

    /// <summary>
    /// What an update that did not finish left behind (the agent restarted, the deploy was cancelled): a replica that
    /// stepped aside as …-N-old next to its successor, and replicas that stopped. The successor stays when it runs and is
    /// healthy, otherwise the old one gets its slot back; stopped replicas are removed — the deploy starts what is
    /// missing. Without this, compose counts them and the next rollout creates too few replicas or none.
    /// </summary>
    private async Task TidyAsync(string[] baseArgs, string project, string service, Func<string, Task> log, CancellationToken ct)
    {
        var none = CancellationToken.None; // half-tidied is worse than a late cancel
        var all = new List<(string Id, string Name, bool Running, string? Health, string Status)>();
        foreach (var id in await _compose.ContainerIdsAsync(baseArgs, service, false, ct))
        {
            try
            {
                var c = await _docker.InspectAsync(id, ct);
                all.Add((id, c.Name.TrimStart('/'), c.State.Running, c.State.Health?.Status, c.State.Status));
            }
            catch (Exception) when (!ct.IsCancellationRequested) { } // gone meanwhile
        }
        var prefix = $"{project}-{service}-";
        foreach (var old in all.Where(c => c.Name.StartsWith(prefix) && c.Name.EndsWith("-old")).ToList())
        {
            var name = old.Name[..^4];
            var successor = all.FirstOrDefault(c => c.Name == name);
            try
            {
                if (successor.Id != null && successor.Running && successor.Health is null or "healthy")
                {
                    await log($"  {old.Name} was left by an update that did not finish; {name} runs — removing it");
                    await _docker.StopAsync(old.Id, DockerService.StopTimeoutSeconds, none);
                    await _docker.RemoveAsync(old.Id, none);
                }
                else
                {
                    await log($"  {old.Name} was left by an update that did not finish — it gets {name} back");
                    if (successor.Id != null) await _docker.TryRemoveAsync(successor.Id, none);
                    await _docker.RenameAsync(old.Id, name, none);
                    if (!old.Running) await _docker.StartAsync(old.Id, none);
                }
            }
            catch (Exception ex) { await log($"  could not tidy {old.Name}: {ex.Message}"); }
            all.RemoveAll(c => c.Id == old.Id || c.Id == successor.Id);
        }
        foreach (var c in all.Where(c => c.Status is "exited" or "created" or "dead"))
        {
            await log($"  removing {c.Name} ({c.Status}): the deploy starts the replicas that are missing");
            await _docker.TryRemoveAsync(c.Id, none);
        }
    }

    /// <param name="old">the running replicas, oldest first</param>
    /// <param name="replace">replace them batch by batch; otherwise they stay and only the missing replicas start</param>
    private async Task<bool> StartReplicasAsync(string[] baseArgs, string project, ServicePlan plan, List<string> old, bool replace, Func<string, Task> log, CancellationToken ct)
    {
        // replace old replicas batch by batch; if there are fewer old than desired, the tail batches only add; more
        // than desired: the ones beyond are drained and removed at the end, like a scale-down
        var batches = new List<List<string>>();
        var replaced = replace ? old.Take(Math.Max(0, plan.Replicas)).ToList() : [];
        for (var i = 0; i < replaced.Count; i += plan.Parallelism) batches.Add(replaced.Skip(i).Take(plan.Parallelism).ToList());
        var surplus = replace ? old.Skip(replaced.Count).ToList() : [];
        var extra = plan.Replicas - old.Count;
        for (var i = 0; i < extra; i += plan.Parallelism) batches.Add([]);

        var current = old.Count;
        var retired = true;
        // stopped leftovers exist too; they are not "new replicas"
        var known = new HashSet<string>(await _compose.ContainerIdsAsync(baseArgs, plan.Name, false, ct));
        // the slot (1..N) each running replica holds; a new replica takes over the slot of the one it replaces
        var slots = new Dictionary<string, int>();
        foreach (var id in old) slots[id] = await SlotAsync(id, project, plan.Name, ct);
        for (var b = 0; b < batches.Count; b++)
        {
            ct.ThrowIfCancellationRequested();
            var batch = batches[b];
            var toStart = batch.Count > 0 ? batch.Count : Math.Min(plan.Parallelism, plan.Replicas - current);
            if (toStart <= 0) break;
            var target = current + toStart;
            await log($"  [{b + 1}/{batches.Count}] starting {toStart} new replica(s), {current} running");
            // compose creates the new replicas, so they carry exactly the current config, but numbers them max+1:
            // they are not started, each becomes a copy that takes over its slot's name and number (TakeSlotAsync)
            var code = await _compose.RunAsync([.. baseArgs, "up", "--no-start", "--no-deps", "--no-recreate", "--scale", $"{plan.Name}={target}", plan.Name], l => log("    " + l), ct);
            if (code != 0) { await log("  ✗ could not create new replicas"); return false; }

            var all = await _compose.ContainerIdsAsync(baseArgs, plan.Name, false, ct);
            var created = all.Where(id => !known.Contains(id)).ToList();
            if (created.Count == 0) { await log("  ✗ no new replicas appeared"); return false; }
            known.UnionWith(created);

            var free = batch.Count > 0 ? batch.Select(id => slots[id]).ToList()
                : Enumerable.Range(1, plan.Replicas + created.Count).Where(n => !slots.ContainsValue(n)).ToList();
            var swaps = new List<SlotSwap>();
            for (var n = 0; n < created.Count && n < free.Count; n++)
            {
                if (await TakeSlotAsync(created[n], n < batch.Count ? batch[n] : null, project, plan.Name, free[n], log) is not { } swap) break;
                swaps.Add(swap);
                known.Add(swap.NewId);
            }
            bool ready;
            try { ready = swaps.Count == created.Count && await WaitReadyAsync(swaps.Select(x => x.NewId).ToList(), plan, log, ct); }
            catch (OperationCanceledException)
            {
                // cancelled while waiting: the old replicas keep their slots, nothing half-made stays behind
                await log("  cancelled → removing the new replica(s), old ones keep running");
                foreach (var x in swaps) await UndoSlotAsync(x);
                foreach (var id in created) await _docker.TryRemoveAsync(id, CancellationToken.None);
                throw;
            }
            if (!ready)
            {
                await log("  ✗ new replica(s) not ready → removing them, old ones keep running");
                foreach (var x in swaps) await UndoSlotAsync(x);
                foreach (var id in created) await _docker.TryRemoveAsync(id, CancellationToken.None);
                return false;
            }
            foreach (var x in swaps) { slots[x.NewId] = x.Slot; if (x.OldId != null) slots.Remove(x.OldId); }

            foreach (var id in batch) retired &= await RetireAsync(id, plan, log, ct);
            current = target - batch.Count;

            if (b < batches.Count - 1 && plan.Delay > TimeSpan.Zero)
            {
                await log($"  waiting {plan.Delay.TotalSeconds:0}s before the next replica");
                await Task.Delay(plan.Delay, ct);
            }
        }

        foreach (var id in surplus) retired &= await RetireAsync(id, plan, log, ct);
        return await SettleAsync(baseArgs, plan, log, ct) && retired;
    }

    /// <summary>
    /// Drains a replica that goes away (pre-stop hook, optionally until its healthcheck fails), stops and removes it;
    /// false when it could not be removed.
    /// </summary>
    private async Task<bool> RetireAsync(string id, ServicePlan plan, Func<string, Task> log, CancellationToken ct)
    {
        var name = await SafeNameAsync(id, ct);
        if (!string.IsNullOrWhiteSpace(plan.PreStopHook))
        {
            await log($"  draining {name}: {plan.PreStopHook}");
            try
            {
                var (exit, output) = await _docker.ExecAsync(id, plan.PreStopHook, HookTimeout, ct);
                if (exit != 0) await log($"  hook exited with {exit} on {name}: {output.Trim()}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { await log($"  hook failed on {name}: {ex.Message}"); }
        }
        if (plan.DrainUntilUnhealthy && plan.HasHealthcheck) await WaitDrainedAsync(id, name, plan, log, ct);
        await log($"  stopping {name}");
        try { await _docker.StopAsync(id, DockerService.StopTimeoutSeconds, ct); } catch (Exception ex) { await log($"  stop failed: {ex.Message}"); }
        try { await _docker.RemoveAsync(id, ct); return true; }
        catch (Exception ex) { await log($"  ✗ remove failed: {ex.Message}"); return false; }
    }

    private async Task<bool> SettleAsync(string[] baseArgs, ServicePlan plan, Func<string, Task> log, CancellationToken ct)
    {
        // compose remembers the last --scale; make sure the desired replica count sticks (also trims extras)
        var final = await _compose.RunAsync([.. baseArgs, "up", "-d", "--no-deps", "--no-recreate", "--scale", $"{plan.Name}={plan.Replicas}", plan.Name], l => log("    " + l), ct);
        if (final != 0) { await log($"  ✗ could not settle on {plan.Replicas} replica(s)"); return false; }
        var ids = await _compose.ContainerIdsAsync(baseArgs, plan.Name, false, ct);
        await PrintStatusAsync(ids, log, ct);
        return true;
    }

    private async Task<bool> UpToDateAsync(string[] baseArgs, ServicePlan plan, string? image, List<string> running, CancellationToken ct) =>
        running.Count == plan.Replicas && await RunsCurrentAsync(baseArgs, plan, image, running, ct);

    /// <summary>
    /// Every running replica runs the service's local image and was created from the current compose config, whatever
    /// their number: compose leaves the replica count out of the config hash.
    /// </summary>
    private async Task<bool> RunsCurrentAsync(string[] baseArgs, ServicePlan plan, string? image, List<string> running, CancellationToken ct)
    {
        if (image == null) return false;
        var all = await _compose.ContainerIdsAsync(baseArgs, plan.Name, false, ct);
        if (all.Count != running.Count) return false; // stopped leftovers: let the rollout clean up
        try
        {
            var imageId = (await _docker.InspectImageAsync(image, ct)).ID;
            var hash = await _compose.ConfigHashAsync(baseArgs, plan.Name, ct);
            if (hash == null) return false;
            foreach (var id in running)
            {
                var c = await _docker.InspectAsync(id, ct);
                if (c.Image != imageId) return false;
                if (c.Config.Labels.Get(DockerService.ConfigHashLabel) != hash) return false;
            }
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return false; }
    }

    private async Task<List<string>> OrderedByCreationAsync(List<string> ids, CancellationToken ct)
    {
        var items = new List<(string id, DateTime created)>();
        foreach (var id in ids)
        {
            try { items.Add((id, DateTimeOffset.TryParse((await _docker.InspectAsync(id, ct)).Created, out var at) ? at.UtcDateTime : DateTime.MaxValue)); }
            catch { items.Add((id, DateTime.MaxValue)); }
        }
        return items.OrderBy(x => x.created).Select(x => x.id).ToList();
    }

    /// <summary>
    /// The slot of a running replica: the number in its name (project-service-N), else compose's number label.
    /// The name comes first: replicas renamed by older agents carry a stale label.
    /// </summary>
    private async Task<int> SlotAsync(string id, string project, string svc, CancellationToken ct)
    {
        var c = await _docker.InspectAsync(id, ct);
        var name = c.Name.TrimStart('/');
        var prefix = $"{project}-{svc}-";
        if (name.StartsWith(prefix) && int.TryParse(name[prefix.Length..], out var fromName)) return fromName;
        return int.TryParse(c.Config.Labels.Get(DockerService.NumberLabel), out var k) ? k : 0;
    }

    /// <summary>
    /// Starts a new replica under slot N's own name and number, project-service-N from its first second: metrics and
    /// log collectors (cAdvisor, Promtail read the name when a container appears) keep one series per slot, and
    /// compose keeps counting 1..N instead of max+1 on every rollout. The old replica of the slot steps aside as
    /// …-old and keeps serving until the new one is ready. The copy is made from the container compose just created,
    /// so it carries exactly the current config.
    /// </summary>
    private async Task<SlotSwap?> TakeSlotAsync(string createdId, string? oldId, string project, string svc, int slot, Func<string, Task> log)
    {
        var none = CancellationToken.None; // a half-done swap is worse than a late cancel
        var name = $"{project}-{svc}-{slot}";
        var oldName = oldId != null ? await SafeNameAsync(oldId, none) : "";
        var stepAside = oldId != null && oldName == name;
        string? copyId = null;
        try
        {
            var made = await _docker.InspectRawAsync(createdId, none) ?? throw new InvalidOperationException("the new replica is gone");
            var madeName = (made["Name"]?.GetValue<string>() ?? "").TrimStart('/');
            // a replica added next to slots 1..N: compose numbered it N+1, which is the free slot — it is that slot
            // already (removing "what holds the name" would remove it)
            if (madeName == name)
            {
                var own = new SlotSwap(createdId, oldId, "", slot);
                if (await _docker.StartAsync(createdId, none)) return own;
                await log($"  ✗ {name} did not start");
                await _docker.TryRemoveAsync(createdId, none);
                return null;
            }

            if (stepAside) await _docker.RenameAsync(oldId!, name + "-old", none);
            else if (oldId == null) await _docker.TryRemoveAsync(name, none); // a stopped leftover holding the name

            // compose gives each replica its own name as a network alias: the copy gets the slot's
            var create = ContainerClone.Parameters(made, null, name, made["Config"]?["Image"]?.GetValue<string>() ?? "", aliasFrom: madeName);
            if (create["Labels"] is JsonObject labels) labels[DockerService.NumberLabel] = slot.ToString();
            await _docker.RemoveAsync(createdId, none);
            copyId = await _docker.CreateCloneAsync(name, create, made, none, aliasFrom: madeName);
            var swap = new SlotSwap(copyId, oldId, stepAside ? oldName : "", slot);
            if (!await _docker.StartAsync(copyId, none))
            {
                await log($"  ✗ {name} did not start");
                await UndoSlotAsync(swap);
                return null;
            }
            return swap;
        }
        catch (Exception ex)
        {
            await log($"  ✗ {name}: {ex.Message}");
            // the copy, if it was made: left behind it would sit in "created" under the slot's name
            if ((copyId ?? (stepAside ? name : null)) is { } copy) await _docker.TryRemoveAsync(copy, none);
            if (stepAside) { try { await _docker.RenameAsync(oldId!, oldName, none); } catch { } }
            return null;
        }
    }

    /// <summary>Removes a new replica and gives its slot's name back to the old one.</summary>
    private async Task UndoSlotAsync(SlotSwap x)
    {
        var none = CancellationToken.None;
        await _docker.TryRemoveAsync(x.NewId, none);
        if (x.OldId != null && x.OldName != "") { try { await _docker.RenameAsync(x.OldId, x.OldName, none); } catch { } }
    }

    /// <summary>
    /// Waits until the old replica's healthcheck fails (the hook made it fail, e.g. `touch /tmp/drain`), so the load
    /// balancer has dropped it, then drain_grace for requests in flight. As long as the health check really takes,
    /// no longer: a fixed `sleep` in the hook is a guess that is either too long or, with slow checks, too short.
    /// </summary>
    private async Task WaitDrainedAsync(string id, string name, ServicePlan plan, Func<string, Task> log, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var deadline = started + plan.Timeout;
        while (true)
        {
            string? health = null;
            try
            {
                var i = await _docker.InspectAsync(id, ct);
                if (!i.State.Running) return;
                health = i.State.Health?.Status;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { return; } // gone already
            if (health is "unhealthy" or null) break;
            if (DateTime.UtcNow > deadline)
            {
                await log($"  {name} still {health} after {plan.Timeout.TotalSeconds:0}s — does the hook make its healthcheck fail? stopping anyway");
                return;
            }
            await Task.Delay(DrainPoll, ct);
        }
        await log($"  {name} unhealthy after {(DateTime.UtcNow - started).TotalSeconds:0}s, {plan.DrainGrace.TotalSeconds:0}s for requests in flight");
        if (plan.DrainGrace > TimeSpan.Zero) await Task.Delay(plan.DrainGrace, ct);
    }
}
