namespace Dockmaster.Server.Api;

// events, metrics and notifications

/// <param name="Container">the container it is about, by name</param>
/// <param name="Details">more to it, e.g. the last log lines of a container that stopped</param>
/// <param name="User">the signed-in user who did it (an audit event)</param>
public sealed record EventDto(int Id, DateTime At, string Level, string Kind, int? ProjectId, string? Project, int? AgentId, string? Agent, string? Service, string Message,
    string? Container = null, string? Details = null, string? User = null);

/// <param name="Points">minute (unix minutes), replicas, cpu (% of a core), cpu limit (cores, 0 = none), memory, memory limit (0 = none), rx, tx (bytes/s)</param>
public sealed record ServiceMetricsDto(string Service, double[][] Points);
public sealed record ProjectMetricsDto(long From, long To, ServiceMetricsDto[] Services);
/// <param name="Points">minute, cpu %, memory used, memory total, disk used, disk total, load1</param>
public sealed record HostMetricsDto(long From, long To, double[][] Points);
/// <param name="Points">minute, cpu (% of a core), cpu limit (cores, 0 = none), memory, memory limit (0 = none), rx, tx (bytes/s)</param>
public sealed record ContainerMetricsDto(long From, long To, double[][] Points);

/// <param name="SeqApiKeySet">an API key is stored (it is never sent back)</param>
public sealed record NotificationSettingsDto(string SeqUrl, bool SeqApiKeySet);
/// <param name="SeqApiKey">null keeps the stored key, "" removes it</param>
public sealed record SaveNotificationSettingsRequest(string SeqUrl, string? SeqApiKey);
public sealed record TestNotificationRequest(string SeqUrl, string? SeqApiKey);
