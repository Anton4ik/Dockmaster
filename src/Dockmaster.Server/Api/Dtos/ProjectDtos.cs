using Dockmaster.Server.Services;

namespace Dockmaster.Server.Api;

// projects, their services, secrets and deployments

/// <param name="EnvHidden">the .env is left out: the user has no Secrets permission (an empty <paramref name="EnvFile"/> then says nothing)</param>
/// <param name="May">what the user may do with the project: the permissions they have on every server it touches</param>
public sealed record ProjectDto(int Id, string Name, int AgentId, string ComposeYaml, string EnvFile, int HealthTimeoutSeconds, DateTime UpdatedAt, DateTime? LastDeployedAt, bool? LastDeployOk, ServiceSummary[] Services,
    int? TemplateId = null, bool EnvHidden = false, string[]? May = null);
/// <param name="Servers">every server the project's services are placed on (the default one first)</param>
/// <param name="Jobs">containers of jobs on a schedule; the ones waiting for their next run are not in <paramref name="Total"/></param>
/// <param name="Template">the template the project is an instance of; null for a project made by hand</param>
public sealed record ProjectListItemDto(int Id, string Name, int AgentId, string AgentName, string[] Services, DateTime UpdatedAt, DateTime? LastDeployedAt, bool? LastDeployOk, int Running, int Total, int Problems, int Updates, string[] Servers,
    int Jobs = 0, string? Template = null);
/// <param name="UpdatedAt">the project's UpdatedAt the page loaded: a save over a newer change (a webhook's tag, a rollback, a limit set on the server page) is refused</param>
public sealed record SaveProjectRequest(string Name, int AgentId, string ComposeYaml, string EnvFile, int HealthTimeoutSeconds, DateTime? UpdatedAt = null);

/// <summary>How a project is spread over servers and what would stop a deploy.</summary>
public sealed record ProjectPlanDto(bool Spanning, HostPlanDto[] Hosts, string[] Problems, string[] Warnings);
/// <param name="Replicas">service → its replicas on this server</param>
/// <param name="Balancers">services this server reaches through a balancer of its own, over their replicas on every server</param>
public sealed record HostPlanDto(int AgentId, string AgentName, bool Online, bool InMesh, string? MeshIp, string[] Services, ExposedDto[] Exposed, string ComposeYaml,
    Dictionary<string, int> Replicas, string[] Balancers);
/// <param name="Address">the address other servers reach the service at; empty when the server is not in the mesh</param>
/// <param name="Balanced">several replicas behind balancers; otherwise the one replica holds the ports on the address</param>
public sealed record ExposedDto(string Service, string[] Ports, string Address, bool Balanced);

/// <param name="AgentIds">the servers the service runs on (several: its replicas are spread); empty = the project's server</param>
/// <param name="AgentId">one server, as older pages send it</param>
public sealed record PlacementRequest(int[]? AgentIds, int? AgentId = null);
public sealed record ExposeRequest(string[] Ports);
public sealed record PlacementResult(ProjectDto Project, ProjectPlanDto Plan);
/// <param name="Ports">several replicas and a fixed host port: "balance" (a balancer holds the port) or "range" (a port each)</param>
public sealed record ServiceLimitsRequest(int? Replicas, string? Memory, string? Cpus, string? Ports = null);
/// <param name="Max">null removes autoscaling</param>
/// <param name="AgentIds">servers extra replicas may go to; none = the servers the service runs on</param>
/// <param name="Ports">as in <see cref="ServiceLimitsRequest"/>, for the replicas the autoscaler starts</param>
public sealed record AutoscaleRequest(int? Max, int? Cpu, int[]? AgentIds, string? Mode, string? Ports = null);
public sealed record LimitsResult(bool Live, string Message, ProjectDto? Project);
public sealed record MountsRequest(MountEdit[]? Mounts);

/// <param name="Volumes">the agent's list as it sent it; null when the server could not be asked (<paramref name="Error"/>)</param>
public sealed record ProjectVolumesServerDto(int AgentId, string AgentName, string? Error, System.Text.Json.Nodes.JsonNode? Volumes);
/// <param name="ImagePaths">service → the paths its image keeps data in (VOLUME), for the images the servers have</param>
public sealed record ProjectVolumesDto(ProjectVolumesServerDto[] Servers, Dictionary<string, string[]> ImagePaths);

public sealed record ProjectSecretDto(string Name, DateTime UpdatedAt);
public sealed record ServiceSecretsRequest(string[]? Names);
/// <summary>What the service form changes; null leaves a part as it is.</summary>
public sealed record ServiceSettingsRequest(string? Image, string[]? Ports, EnvVar[]? Environment, string? Restart, ScheduleRequest? Schedule = null);
/// <summary>A job's schedule as the form sends it; no <paramref name="Cron"/> takes the schedule off (the service is kept running again).</summary>
public sealed record ScheduleRequest(string? Cron, string? TimeZone, string? Timeout);
/// <param name="Next">the next times the expression names, UTC</param>
public sealed record SchedulePreviewDto(bool Ok, string? Error, string Description, DateTime[] Next, string? ZoneProblem);
public sealed record JobRunResult(bool Ok, string Message);
/// <summary>A new service from the form (or a ready-made app): its data comes as mounts, like the Data editor sends them.</summary>
public sealed record NewServiceRequest(string Name, string Image, string[]? Ports, EnvVar[]? Environment, string? Restart, MountEdit[]? Mounts, ScheduleRequest? Schedule = null);
/// <param name="Hidden">the content is not shown: it takes the Secrets permission</param>
/// <param name="Size">bytes of the content (also when it is hidden)</param>
public sealed record ProjectFileDto(string Name, string Content, DateTime UpdatedAt, bool Hidden = false, int Size = 0);
public sealed record SaveFileRequest(string? Content);
public sealed record SetSecretRequest(string Value);
/// <param name="Source">who started it: the signed-in user ("anna", "anna: rollback to #12"), "webhook", an API key</param>
/// <param name="HasFiles">the compose file and .env it put out are kept: what changed since, and going back to it</param>
public sealed record DeploymentDto(int Id, string Action, DateTime StartedAt, DateTime? FinishedAt, bool? Ok, string Message, string Source, bool HasFiles);
/// <summary>A deploy's files next to the project's files now.</summary>
public sealed record DeploymentFilesDto(string ComposeYaml, string EnvFile, string CurrentComposeYaml, string CurrentEnvFile, bool EnvHidden = false);
/// <param name="Path">the webhook's path under the UI's address (the page puts its own origin in front); null = none</param>
public sealed record ProjectHookDto(string? Path);
