using Dockmaster.Protocol;
using Dockmaster.Server.Data;

namespace Dockmaster.Server.Services;

/// <summary>Image references and the registry that serves them: one rule for deploys, re-creates and update checks.</summary>
public static class ImageRef
{
    public const string DockerHub = "registry-1.docker.io";

    /// <summary>host, repository and tag of an image reference; null for ids and digests.</summary>
    public static (string Host, string Repo, string Tag)? Parse(string? image)
    {
        if (string.IsNullOrWhiteSpace(image) || image.StartsWith("sha256:") || image.Contains('@')) return null;
        var host = ExplicitHost(image);
        var rest = host != null ? image[(host.Length + 1)..] : image;
        host = host is null or "docker.io" or "index.docker.io" ? DockerHub : host;
        var colon = rest.LastIndexOf(':');
        var tagged = colon > 0 && colon > rest.LastIndexOf('/');
        var tag = tagged ? rest[(colon + 1)..] : "latest";
        var repo = tagged ? rest[..colon] : rest;
        if (host == DockerHub && !repo.Contains('/')) repo = "library/" + repo;
        return repo.Length == 0 ? null : (host, repo, tag);
    }

    /// <summary>The registry host written in the reference ("registry:5000/app" → "registry:5000"); null for Docker Hub names.</summary>
    public static string? ExplicitHost(string image)
    {
        var slash = image.IndexOf('/');
        if (slash <= 0) return null;
        var first = image[..slash];
        return first.Contains('.') || first.Contains(':') || first == "localhost" ? first : null;
    }

    /// <summary>The stored registry for a host, under its main address or an alias (Docker Hub under any of its names).</summary>
    public static RegistryEntity? RegistryFor(IEnumerable<RegistryEntity> registries, string host) =>
        registries.FirstOrDefault(r => r.Serves(host)) ??
        (host == DockerHub ? registries.FirstOrDefault(r => r.Url is "docker.io" or "index.docker.io" or DockerHub) : null);

    /// <summary>`docker login` entries for the registries these images come from, for those with a stored login.</summary>
    public static List<RegistryCredential> Credentials(IEnumerable<string> images, IReadOnlyCollection<RegistryEntity> registries, RegistryClient client)
    {
        var result = new Dictionary<string, RegistryCredential>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in images.Select(Parse).OfType<(string Host, string Repo, string Tag)>().Select(p => p.Host).Distinct())
        {
            if (RegistryFor(registries, host) is not { Username.Length: > 0 } reg) continue;
            var c = client.For(reg);
            var server = host == DockerHub ? "docker.io" : host; // the name docker login knows Docker Hub by
            result.TryAdd(server, new RegistryCredential { Server = server, Username = c.Username, Password = c.Password });
        }
        return [.. result.Values];
    }
}
