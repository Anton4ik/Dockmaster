using System.Net;
using System.Text.Json.Serialization;

namespace Dockmaster.Agent.Docker;

// What the agent reads of the Docker Engine API's answers: only the fields it uses. Read with case-insensitive names
// (Docker writes "Id", "NanoCpus", "HostIp"...), so a field Docker adds or drops changes nothing here. Where a whole
// configuration must go back to Docker unchanged (a copy of a container), the raw JSON is used instead (ContainerClone).

/// <summary>Docker answered with an error; <see cref="Exception.Message"/> is its own message ("No such container: x").</summary>
public sealed class DockerApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = status;
}

public sealed class ContainerInspectResponse
{
    public string ID { get; set; } = "";
    public string Name { get; set; } = "";
    public string Created { get; set; } = "";
    public string Path { get; set; } = "";
    public List<string>? Args { get; set; }
    public string Platform { get; set; } = "";
    public string Image { get; set; } = "";
    public long RestartCount { get; set; }
    public ContainerState State { get; set; } = new();
    public Config Config { get; set; } = new();
    public HostConfig HostConfig { get; set; } = new();
    public NetworkSettings? NetworkSettings { get; set; }
    public List<MountPoint>? Mounts { get; set; }
}

public sealed class ContainerState
{
    public string Status { get; set; } = "";
    public bool Running { get; set; }
    public bool OOMKilled { get; set; }
    public long ExitCode { get; set; }
    public string Error { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string FinishedAt { get; set; } = "";
    public Health? Health { get; set; }
}

public sealed class Health
{
    public string Status { get; set; } = "";
    public long FailingStreak { get; set; }
    public List<HealthcheckResult>? Log { get; set; }
}

public sealed class HealthcheckResult
{
    public string Start { get; set; } = "";
    public string End { get; set; } = "";
    public long ExitCode { get; set; }
    public string? Output { get; set; }
}

public sealed class Config
{
    public string Hostname { get; set; } = "";
    public string User { get; set; } = "";
    public List<string>? Env { get; set; }
    public List<string>? Cmd { get; set; }
    public List<string>? Entrypoint { get; set; }
    public string WorkingDir { get; set; } = "";
    public string Image { get; set; } = "";
    public Dictionary<string, string>? Labels { get; set; }
    public bool Tty { get; set; }
    public HealthConfig? Healthcheck { get; set; }
    public Dictionary<string, object>? Volumes { get; set; }
}

/// <summary>Durations in nanoseconds, as Docker gives them.</summary>
public sealed class HealthConfig
{
    public List<string>? Test { get; set; }
    public long Interval { get; set; }
    public long Timeout { get; set; }
    public long Retries { get; set; }
    public long StartPeriod { get; set; }
    public long StartInterval { get; set; }
}

public sealed class HostConfig
{
    public string NetworkMode { get; set; } = "";
    public RestartPolicy? RestartPolicy { get; set; }
    public long Memory { get; set; }
    public long MemorySwap { get; set; }
    public long MemoryReservation { get; set; }
    public long NanoCPUs { get; set; }
    public long CPUShares { get; set; }
    public bool Privileged { get; set; }
    public LogConfig? LogConfig { get; set; }
    public Dictionary<string, List<PortBinding>?>? PortBindings { get; set; }
}

/// <summary>Name as Docker spells it: "no" (or ""), "always", "unless-stopped", "on-failure".</summary>
public sealed class RestartPolicy
{
    public string Name { get; set; } = "";
    public long MaximumRetryCount { get; set; }
}

public sealed class LogConfig
{
    public string Type { get; set; } = "";
}

public sealed class PortBinding
{
    public string HostIP { get; set; } = "";
    public string HostPort { get; set; } = "";
}

public sealed class NetworkSettings
{
    public Dictionary<string, EndpointSettings>? Networks { get; set; }
    public Dictionary<string, List<PortBinding>?>? Ports { get; set; }
}

public sealed class EndpointSettings
{
    public List<string>? Aliases { get; set; }
    public string NetworkID { get; set; } = "";
    public string IPAddress { get; set; } = "";
    public long IPPrefixLen { get; set; }
    public string Gateway { get; set; } = "";
    public string MacAddress { get; set; } = "";
}

public sealed class MountPoint
{
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Mode { get; set; } = "";
    public bool RW { get; set; }
}

/// <summary>One entry of GET /containers/json.</summary>
public sealed class ContainerListResponse
{
    public string ID { get; set; } = "";
    public List<string>? Names { get; set; }
    public string Image { get; set; } = "";
    public string ImageID { get; set; } = "";
    public string State { get; set; } = "";
    public string Status { get; set; } = "";
    public Dictionary<string, string>? Labels { get; set; }
    public List<MountPoint>? Mounts { get; set; }
    public NetworkSettings? NetworkSettings { get; set; }
}

public sealed class ImageInspectResponse
{
    public string ID { get; set; } = "";
    public List<string>? RepoDigests { get; set; }
    public Config? Config { get; set; }
}

/// <summary>One entry of GET /images/json.</summary>
public sealed class ImagesListResponse
{
    public string ID { get; set; } = "";
    public List<string>? RepoTags { get; set; }
    public long Size { get; set; }
    /// <summary>Unix seconds.</summary>
    public long Created { get; set; }
}

public sealed class ImagesPruneResponse
{
    public List<ImageDeleteResponse>? ImagesDeleted { get; set; }
    public ulong SpaceReclaimed { get; set; }
}

public sealed class ImageDeleteResponse
{
    public string? Untagged { get; set; }
    public string? Deleted { get; set; }
}

public sealed class NetworkResponse
{
    public string Name { get; set; } = "";
    public string ID { get; set; } = "";
    public string Created { get; set; } = "";
    public string Scope { get; set; } = "";
    public string Driver { get; set; } = "";
    public bool Internal { get; set; }
    public IPAM? IPAM { get; set; }
    public Dictionary<string, string>? Labels { get; set; }
}

public sealed class IPAM
{
    public List<IPAMConfig>? Config { get; set; }
}

public sealed class IPAMConfig
{
    public string Subnet { get; set; } = "";
}

public sealed class VolumesListResponse
{
    public List<VolumeResponse>? Volumes { get; set; }
}

public sealed class VolumeResponse
{
    public string Name { get; set; } = "";
    public string Driver { get; set; } = "";
    public string Mountpoint { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public Dictionary<string, string>? Labels { get; set; }
    public Dictionary<string, string>? Options { get; set; }
}

public sealed class VersionResponse
{
    public string Version { get; set; } = "";
    public string Os { get; set; } = "";
    public string Arch { get; set; } = "";
    public string ApiVersion { get; set; } = "";
}

public sealed class SystemInfoResponse
{
    public long NCPU { get; set; }
    public long MemTotal { get; set; }
    public string DockerRootDir { get; set; } = "";
    public string OperatingSystem { get; set; } = "";
    public bool CPUCfsQuota { get; set; }
}

public sealed class ContainerExecInspectResponse
{
    public long ExitCode { get; set; }
    public bool Running { get; set; }
}

/// <summary>One event of GET /events.</summary>
public sealed class Message
{
    public string Type { get; set; } = "";
    public string Action { get; set; } = "";
    public Actor? Actor { get; set; }
    [JsonPropertyName("id")] public string ID { get; set; } = "";
    [JsonPropertyName("time")] public long Time { get; set; }
    [JsonPropertyName("timeNano")] public long TimeNano { get; set; }
}

public sealed class Actor
{
    public string ID { get; set; } = "";
    public Dictionary<string, string>? Attributes { get; set; }
}
