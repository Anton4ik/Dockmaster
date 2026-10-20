namespace Dockmaster.Server.Api;

// servers (agents), their containers and images

public sealed record HostStatsDto(double CpuPercent, long MemoryUsed, long MemoryTotal, long DiskUsed, long DiskTotal, double Load1, long UptimeSeconds);

public sealed record ContainerDto(string Id, string Name, string Image, string State, string Health, string Status, long CreatedUnix, long StartedUnix,
    int RestartCount, string Project, string Service, double CpuPercent, long MemoryUsage, long MemoryLimit, double CpuLimit, long NetRx, long NetTx, string[] Ports,
    string ImageRef, string Update, string? UpdateDetail, string RestartPolicy, int RestartMax, int ExitCode, ContainerExitDto? LastExit,
    bool Job = false, // a job on a schedule: exited with 0 is "finished", not "stopped"
    Dockmaster.Server.Services.JobInfoDto? JobInfo = null); // its schedule in words, the next time, how its last run went

/// <summary>How the container's last run ended (its log lines are in the event of it).</summary>
public sealed record ContainerExitDto(long UnixMs, int ExitCode, bool Oom);

public sealed record AgentDto(int Id, string Name, bool Online, DateTime? LastSeenAt, string? Hostname, string? Os, string? Arch, string? DockerVersion,
    int Cpus, long MemoryTotal, string? AgentIp, string? AgentVersion, HostStatsDto? Stats, int Running, int Total, int Unhealthy, long StatsUnixMs, bool CpuLimits, string AgentImage,
    long LimitMemory, double LimitCpus, // limits of the running containers added up, to compare with the host
    string? MeshIp, bool MeshUp,
    int Jobs = 0); // containers of jobs on a schedule (the ones waiting for their next run are not in Total)

public sealed record AgentDetailDto(AgentDto Agent, ContainerDto[] Containers);

public sealed record NewAgentRequest(string Name, string PublicUrl);
public sealed record NewAgentResponse(int Id, string Token, string Fingerprint, string Command);
public sealed record RenameAgentRequest(string Name);

public sealed record ImportProjectRequest(string Project);
public sealed record ImportProjectResponse(ProjectDto Project, string[] Warnings);
/// <summary>What the agent answers to an import: the compose project as `docker compose config` sees it.</summary>
internal sealed record ImportedProject(string Compose, string Env, string Source, string[] ConfigFiles, string[] Warnings);

public sealed record ContainerLimitsRequest(string? Memory, string? Cpus);
public sealed record ContainerNetworkRequest(string? Network, bool Connect);
public sealed record ContainerRestartPolicyRequest(string? Policy, int? MaxRetries);
public sealed record RemoveImagesRequest(string[]? Ids);

/// <param name="Kind">local (on this server), nfs or cifs (a shared folder of another machine, e.g. a NAS)</param>
/// <param name="Address">nfs / cifs: the machine that shares the folder</param>
/// <param name="Path">nfs: the exported folder (/volume1/data); cifs: the share's name</param>
/// <param name="Version">nfs: 3 or 4 (empty: whatever the server offers); cifs: SMB version (default 3.0)</param>
/// <param name="Extra">more mount options, comma separated</param>
public sealed record CreateVolumeRequest(string Name, string? Kind, string? Address, string? Path, string? Username, string? Password, string? Version, string? Extra);
public sealed record RemoveVolumesRequest(string[]? Names);
public sealed record CreateNetworkRequest(string? Name, bool Internal);
/// <param name="ToName">the volume on that server; empty = the same name</param>
/// <param name="Wipe">empty the target first (otherwise the files are added, same names replaced)</param>
public sealed record CopyVolumeRequest(int ToAgentId, string? ToName, bool Wipe);
