using Dockmaster.Server.Services;

namespace Dockmaster.Server.Api;

// templates, their instances and the API keys that manage them

/// <param name="AgentIds">servers instances may go to; empty = any</param>
/// <param name="Instances">how many instances there are now</param>
/// <param name="Warnings">variables used where the caller's value decides what a container may reach (host paths, ports, the image)</param>
public sealed record TemplateDto(int Id, string Name, string Description, string ComposeYaml, string EnvFile, TemplateVariable[] Variables, int[] AgentIds,
    int MaxInstances, int HealthTimeoutSeconds, int Instances, DateTime UpdatedAt, string[] Warnings);
public sealed record SaveTemplateRequest(string Name, string? Description, string ComposeYaml, string? EnvFile, TemplateVariable[]? Variables, int[]? AgentIds,
    int MaxInstances, int HealthTimeoutSeconds);

public sealed record ApiKeyDto(int Id, string Name, string Hint, int[] TemplateIds, string[] Templates, DateTime CreatedAt, DateTime? LastUsedAt);
public sealed record NewApiKeyRequest(string Name, int[]? TemplateIds);
/// <param name="Key">the key itself: shown this once, only its hash is kept</param>
public sealed record NewApiKeyResponse(ApiKeyDto ApiKey, string Key);

/// <param name="State">new (never deployed), deploying, running, starting (health check pending), unhealthy, partial, stopped</param>
public sealed record InstanceDto(string Name, string Template, int ProjectId, string Server, string State, int Running, int Total,
    Dictionary<string, string> Variables, DateTime CreatedAt, DateTime? LastDeployedAt, bool? LastDeployOk, double CpuPercent, long MemoryUsage);
/// <param name="Server">the server to run it on, by name; empty = the allowed online server with the most memory free</param>
/// <param name="Deploy">false only saves it (deploy or start it later); default true</param>
public sealed record CreateInstanceRequest(string Name, Dictionary<string, string>? Variables, string? Server, bool? Deploy);
/// <param name="Deploy">false only saves the values (applied on the next deploy); default true</param>
public sealed record UpdateInstanceRequest(Dictionary<string, string>? Variables, bool? Deploy);
public sealed record InstanceResult(bool Ok, string Message, InstanceDto? Instance);
