using System.Reflection;

namespace Dockmaster.Protocol;

public static class BuildInfo
{
    /// <summary>The commit CI built from ("dev" for local builds).</summary>
    public static string Version(Assembly assembly)
    {
        var v = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
        var plus = v.IndexOf('+');
        return plus > 0 ? v[..plus] : v;
    }
}
