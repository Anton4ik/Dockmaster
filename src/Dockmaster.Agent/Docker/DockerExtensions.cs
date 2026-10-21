namespace Dockmaster.Agent.Docker;

/// <summary>Small readings of the Engine API's answers that several places need.</summary>
internal static class DockerExtensions
{
    /// <summary>A label's value, "" when the label (or the whole map) is missing.</summary>
    public static string Get(this IDictionary<string, string>? labels, string key) =>
        labels != null && labels.TryGetValue(key, out var v) ? v : "";

    /// <summary>The name a person knows the container by: its name without the slash, else its short id.</summary>
    public static string DisplayName(this ContainerListResponse c) => c.Names?.FirstOrDefault()?.TrimStart('/') ?? c.ID[..12];
}
