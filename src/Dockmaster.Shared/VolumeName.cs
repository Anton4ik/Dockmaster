using System.Text.RegularExpressions;

namespace Dockmaster.Protocol;

/// <summary>
/// What docker takes as a volume name. The agent checks it too: a helper binds the name, and a path there ("/") would
/// bind the host's folder instead of a volume.
/// </summary>
public static partial class VolumeName
{
    public static bool IsValid(string? name) => name != null && Pattern().IsMatch(name);

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,254}$")] private static partial Regex Pattern();
}
