using System.Security.Cryptography;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace Dockmaster.Server.Services;

/// <summary>
/// A project's files (nginx.conf, appsettings.json…): kept on the server, written by the agent to &lt;project&gt;/files/
/// on every deploy, and mounted like any folder of the server: `./files/nginx.conf:/etc/nginx/nginx.conf:ro`.
/// </summary>
public static class ComposeFiles
{
    public const string Folder = "./files/";
    public const string StampLabel = "dockmaster.files";
    public const int MaxSize = 1 << 20;
    public const int MaxCount = 100;
    /// <summary>All files of a project together: they travel to the agent in one message with the compose file.</summary>
    public const int MaxTotalSize = 8 << 20;

    public static string? ValidateName(string name) =>
        ComposeEditor.TagRegex().IsMatch(name) ? null : "a file name is letters, digits, '.', '-' and '_', e.g. nginx.conf";

    /// <summary>The project files a service mounts (by name): its bind mounts from ./files/.</summary>
    public static IEnumerable<string> UsedBy(ServiceSummary s) =>
        s.Mounts.Where(m => m.Type == "bind" && m.Source.StartsWith(Folder)).Select(m => m.Source[Folder.Length..].Split('/')[0]).Distinct();

    /// <summary>
    /// A label on every service that mounts project files, made of their contents: compose re-creates a container
    /// only when its configuration changed, and a new nginx.conf alone would not count. Services whose labels are
    /// written inline are left as they are (a person can deploy with force).
    /// </summary>
    public static string Stamp(string yaml, string project, IReadOnlyDictionary<string, string> files, Action<string>? warn = null)
    {
        var stamped = yaml;
        foreach (var s in ComposeServices.Parse(yaml, project))
        {
            var used = UsedBy(s).Where(files.ContainsKey).Order(StringComparer.Ordinal).ToList();
            if (used.Count == 0) continue;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(used.Select(n => n + "\0" + files[n] + "\0")))))[..16].ToLowerInvariant();
            if (ComposeEditor.TryEdit(stamped, y => SetLabel(y, s.Name, hash), out var edited, out var refused)) stamped = edited;
            else warn?.Invoke($"service {s.Name}: a changed file does not re-create it by itself ({refused})");
        }
        return stamped;
    }

    /// <summary>Warnings for what the services mount from ./files/ that is not a project file: docker makes an empty folder in its place.</summary>
    public static IEnumerable<string> Missing(IEnumerable<ServiceSummary> services, IReadOnlyCollection<string> names) =>
        services.SelectMany(s => UsedBy(s).Where(n => !names.Contains(n))
            .Select(n => $"service {s.Name} mounts {Folder}{n}, but the project has no file {n} — unless the server has it there already, docker makes an empty folder in its place (Files tab)"));

    private static string SetLabel(string yaml, string service, string value)
    {
        var labels = (YamlNodes.LoadRoot(yaml).Child("services", service) as YamlMappingNode).Child("labels");
        if (labels is YamlSequenceNode seq)
            return ComposeEditor.SetSequence(yaml, service, "labels",
                [.. seq.Children.OfType<YamlScalarNode>().Select(x => x.Value ?? "").Where(x => !x.StartsWith(StampLabel + "=")), $"{StampLabel}={value}"]);
        return ComposeEditor.SetLabel(yaml, service, StampLabel, value);
    }
}
