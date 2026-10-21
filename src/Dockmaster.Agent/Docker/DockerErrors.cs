namespace Dockmaster.Agent.Docker;

/// <summary>What a person should read when the docker CLI fails (Docker's API errors carry their own message, see <see cref="DockerApiException"/>).</summary>
public static class DockerErrors
{
    /// <summary>The last non-blank line of a command's output, trimmed: where CLI tools put the error. Null when there is none.</summary>
    public static string? LastLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
}
