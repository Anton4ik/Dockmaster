using System.Text.Json;

namespace Dockmaster.Agent.Docker;

// The heartbeat's calls: for every container on every heartbeat, so read straight off the JSON into just the fields
// the heartbeat sends (no models in between).
public sealed partial class EngineApi
{
    public sealed record Port(string Ip, int PrivatePort, int PublicPort, string Type);

    public sealed record ListedContainer(string Id, string Name, string Image, string State, string Status, long Created,
        IReadOnlyDictionary<string, string> Labels, IReadOnlyList<Port> Ports);

    public sealed record ContainerSummary(int RestartCount, double CpuLimit, long MemoryLimit, string RestartPolicy, int RestartMax, int ExitCode,
        bool OomKilled, long StartedUnix, string Health, string ImageRef, string ImageId);

    /// <summary>
    /// <paramref name="Platform"/>: on the containerd image store, where <paramref name="Id"/> is the digest of an OCI
    /// index, the manifest of the image this host runs from it; "" on the classic store (the id is the config digest).
    /// </summary>
    public sealed record ImageSummary(string Id, IReadOnlyList<string> RepoDigests, string Platform = "");

    public sealed record Stats(ulong Cpu, ulong System, ulong PreCpu, ulong PreSystem, uint OnlineCpus, int PerCpuCount,
        ulong MemoryUsage, ulong InactiveFile, long NetRx, long NetTx, ulong MaxUsage = 0);

    public async Task<List<ListedContainer>> ListForHeartbeatAsync(CancellationToken ct)
    {
        using var doc = await GetDocAsync("containers/json?all=1", ct) ?? throw new DockerApiException(System.Net.HttpStatusCode.NotFound, "docker: no container list");
        return ParseList(doc.RootElement);
    }

    /// <summary>Null when the container is gone.</summary>
    public async Task<ContainerSummary?> InspectSummaryAsync(string id, CancellationToken ct)
    {
        using var doc = await GetDocAsync($"containers/{E(id)}/json", ct);
        return doc == null ? null : ParseInspect(doc.RootElement);
    }

    /// <summary>Null when there is no such image.</summary>
    public async Task<ImageSummary?> InspectImageSummaryAsync(string nameOrId, CancellationToken ct)
    {
        // manifests=1 (API 1.48+) lists what an index holds; an older docker ignores it
        using var doc = await GetDocAsync($"images/{E(nameOrId)}/json?manifests=1", ct);
        return doc == null ? null : ParseImage(doc.RootElement, HostArch);
    }

    /// <summary>One reading of the container's counters (one-shot: no second reading a second later). Null when it is gone.</summary>
    public async Task<Stats?> StatsAsync(string id, CancellationToken ct)
    {
        using var doc = await GetDocAsync($"containers/{E(id)}/stats?stream=false&one-shot=true", ct);
        return doc == null ? null : ParseStats(doc.RootElement);
    }

    public static List<ListedContainer> ParseList(JsonElement root)
    {
        var list = new List<ListedContainer>(root.GetArrayLength());
        foreach (var c in root.EnumerateArray())
        {
            var id = Str(c, "Id");
            var name = c.TryGetProperty("Names", out var names) && names.ValueKind == JsonValueKind.Array && names.GetArrayLength() > 0
                ? (names[0].GetString() ?? "").TrimStart('/') : "";
            var labels = new Dictionary<string, string>();
            if (c.TryGetProperty("Labels", out var l) && l.ValueKind == JsonValueKind.Object)
                foreach (var p in l.EnumerateObject()) labels[p.Name] = p.Value.GetString() ?? "";
            var ports = new List<Port>();
            if (c.TryGetProperty("Ports", out var ps) && ps.ValueKind == JsonValueKind.Array)
                foreach (var p in ps.EnumerateArray())
                    ports.Add(new Port(Str(p, "IP"), (int)Num(p, "PrivatePort"), (int)Num(p, "PublicPort"), Str(p, "Type")));
            list.Add(new ListedContainer(id, name.Length > 0 ? name : id[..Math.Min(12, id.Length)], Str(c, "Image"), Str(c, "State"), Str(c, "Status"),
                Num(c, "Created"), labels, ports));
        }
        return list;
    }

    public static ContainerSummary ParseInspect(JsonElement c)
    {
        var host = Obj(c, "HostConfig");
        var state = Obj(c, "State");
        var policy = host is { } h ? Obj(h, "RestartPolicy") : null;
        long started = 0;
        if (state is { } s && DateTimeOffset.TryParse(Str(s, "StartedAt"), out var st) && st.Year > 2000) started = st.ToUnixTimeSeconds();
        var health = state is { } s2 && Obj(s2, "Health") is { } hh ? Str(hh, "Status") : "";
        return new ContainerSummary(
            (int)Num(c, "RestartCount"),
            host is { } h1 ? Num(h1, "NanoCpus") / 1e9 : 0,
            host is { } h2 ? Num(h2, "Memory") : 0,
            policy is { } p ? DockerService.RestartPolicyName(Str(p, "Name")) : "no",
            policy is { } p2 ? (int)Num(p2, "MaximumRetryCount") : 0,
            state is { } s3 ? (int)Num(s3, "ExitCode") : 0,
            state is { } s4 && s4.TryGetProperty("OOMKilled", out var oom) && oom.ValueKind == JsonValueKind.True,
            started,
            health,
            Obj(c, "Config") is { } cfg ? Str(cfg, "Image") : "",
            Str(c, "Image"));
    }

    public static ImageSummary ParseImage(JsonElement i, string arch = "amd64")
    {
        var digests = new List<string>();
        if (i.TryGetProperty("RepoDigests", out var d) && d.ValueKind == JsonValueKind.Array)
            foreach (var x in d.EnumerateArray()) if (x.GetString() is { } s) digests.Add(s);
        return new ImageSummary(Str(i, "Id"), digests, PlatformImage(i, arch));
    }

    /// <summary>
    /// The manifest of the image a container on this host runs from an index: the pulled child of kind "image" for the
    /// host's architecture (or the only pulled one), skipping BuildKit's attestation manifests. "" when docker lists none.
    /// </summary>
    private static string PlatformImage(JsonElement i, string arch)
    {
        if (!i.TryGetProperty("Manifests", out var ms) || ms.ValueKind != JsonValueKind.Array) return "";
        string? any = null;
        foreach (var m in ms.EnumerateArray())
        {
            if (Str(m, "Kind") != "image" || !m.TryGetProperty("Available", out var a) || a.ValueKind != JsonValueKind.True) continue;
            var id = Str(m, "ID");
            if (id.Length == 0) continue;
            var platform = Obj(m, "ImageData") is { } data ? Obj(data, "Platform") : null;
            if (platform is { } p && Str(p, "architecture") == arch) return id;
            any ??= id;
        }
        return any ?? "";
    }

    // the agent runs on the host it manages: its architecture is the host's, in docker's words
    private static readonly string HostArch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "amd64",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.Arm => "arm",
        System.Runtime.InteropServices.Architecture.X86 => "386",
        var a => a.ToString().ToLowerInvariant(),
    };

    public static Stats ParseStats(JsonElement s)
    {
        ulong cpu = 0, system = 0, preCpu = 0, preSystem = 0, usage = 0, inactive = 0, max = 0;
        uint online = 0;
        var perCpu = 0;
        if (Obj(s, "cpu_stats") is { } cs)
        {
            if (Obj(cs, "cpu_usage") is { } cu)
            {
                cpu = UNum(cu, "total_usage");
                if (cu.TryGetProperty("percpu_usage", out var pc) && pc.ValueKind == JsonValueKind.Array) perCpu = pc.GetArrayLength();
            }
            system = UNum(cs, "system_cpu_usage");
            online = (uint)UNum(cs, "online_cpus");
        }
        if (Obj(s, "precpu_stats") is { } ps)
        {
            if (Obj(ps, "cpu_usage") is { } pu) preCpu = UNum(pu, "total_usage");
            preSystem = UNum(ps, "system_cpu_usage");
        }
        if (Obj(s, "memory_stats") is { } ms)
        {
            usage = UNum(ms, "usage");
            max = UNum(ms, "max_usage"); // cgroup v1 only
            if (Obj(ms, "stats") is { } mst)
                inactive = mst.TryGetProperty("inactive_file", out _) ? UNum(mst, "inactive_file") : UNum(mst, "total_inactive_file");
        }
        long rx = 0, tx = 0;
        if (Obj(s, "networks") is { } nets)
            foreach (var n in nets.EnumerateObject())
            {
                rx += (long)UNum(n.Value, "rx_bytes");
                tx += (long)UNum(n.Value, "tx_bytes");
            }
        return new Stats(cpu, system, preCpu, preSystem, online, perCpu, usage, inactive, rx, tx, max);
    }

    private static JsonElement? Obj(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static ulong UNum(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt64(out var n) ? n : 0;
}
