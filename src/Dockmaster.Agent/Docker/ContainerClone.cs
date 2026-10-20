using System.Text.Json.Nodes;

namespace Dockmaster.Agent.Docker;

/// <summary>
/// A new container with the configuration of an existing one, running another image of the same name. Works on the
/// inspect as Docker sent it (<see cref="EngineApi.InspectContainerRawAsync"/>): the config and host config go back
/// whole, so nothing Docker knows is lost on the way — Docker.DotNet's models are of an older API and dropped what
/// they did not know (a healthcheck's start_interval on every rolling update).
/// </summary>
public static class ContainerClone
{
    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static string NetworkMode(JsonObject c) => Str(c["HostConfig"]?["NetworkMode"]);

    private static bool OwnNetworking(JsonObject c) => NetworkMode(c) is "host" or "none" || NetworkMode(c).StartsWith("container:");

    private static JsonObject Networks(JsonObject c) => c["NetworkSettings"]?["Networks"] as JsonObject ?? [];

    /// <summary>The network a create call joins: the one named as network mode, else the first; none with own networking.</summary>
    private static string? PrimaryNetwork(JsonObject c)
    {
        if (OwnNetworking(c)) return null;
        var names = Networks(c).Select(n => n.Key).ToList();
        return names.FirstOrDefault(n => n == NetworkMode(c)) ?? names.FirstOrDefault();
    }

    /// <summary>
    /// The body of a create call. Mounts, ports, restart policy, env, labels, the first network and its aliases.
    /// Everything the container got from the old image — env, labels, command, entrypoint, working dir, user,
    /// healthcheck, exposed ports, volumes — is left out so the new image brings its own (an image that moved its app
    /// from / to /app must not be started in /); Docker's default hostname (the old id) too. Without the old image
    /// everything is kept. <paramref name="aliasFrom"/>: an alias of the old container's own name that becomes
    /// <paramref name="name"/> (compose gives each replica its name as an alias).
    /// </summary>
    public static JsonObject Parameters(JsonObject old, JsonObject? oldImage, string name, string image, string? aliasFrom = null)
    {
        var cfg = old["Config"]?.DeepClone() as JsonObject ?? [];
        var img = oldImage?["Config"] as JsonObject;
        var id = Str(old["Id"]);
        cfg["Image"] = image;
        if (id.Length >= 12 && Str(cfg["Hostname"]) == id[..12]) cfg.Remove("Hostname");

        if (img != null)
        {
            var imageEnv = (img["Env"] as JsonArray ?? []).Select(Str).ToHashSet();
            if (cfg["Env"] is JsonArray env) cfg["Env"] = new JsonArray(env.Where(e => !imageEnv.Contains(Str(e))).Select(e => e?.DeepClone()).ToArray());
            var imageLabels = img["Labels"] as JsonObject ?? [];
            if (cfg["Labels"] is JsonObject labels)
                foreach (var l in labels.ToList())
                    if (imageLabels.TryGetPropertyValue(l.Key, out var v) && Str(v) == Str(l.Value)) labels.Remove(l.Key);
            var ownEntrypoint = !Same(cfg["Entrypoint"], img["Entrypoint"]);
            if (!ownEntrypoint) cfg.Remove("Entrypoint");
            // with its own entrypoint Docker ignores the image's command, so the command stays as it was
            if (!ownEntrypoint && Same(cfg["Cmd"], img["Cmd"])) cfg.Remove("Cmd");
            foreach (var f in new[] { "WorkingDir", "User", "StopSignal", "Shell", "Healthcheck" })
                if (Same(cfg[f], img[f])) cfg.Remove(f);
            foreach (var f in new[] { "ExposedPorts", "Volumes" })
                if (cfg[f] is JsonObject own && img[f] is JsonObject fromImage)
                {
                    foreach (var k in own.Select(e => e.Key).Where(fromImage.ContainsKey).ToList()) own.Remove(k);
                    if (own.Count == 0) cfg.Remove(f);
                }
        }

        cfg["HostConfig"] = old["HostConfig"]?.DeepClone();
        if (PrimaryNetwork(old) is { } primary)
            cfg["NetworkingConfig"] = new JsonObject
            {
                ["EndpointsConfig"] = new JsonObject { [primary] = Endpoint(old, Networks(old)[primary], name, aliasFrom) },
            };
        return cfg;
    }

    /// <summary>The networks beyond the first one (a create call joins only one), with what to join each with.</summary>
    public static IEnumerable<(string network, JsonObject endpoint)> OtherNetworks(JsonObject old, string name, string? aliasFrom = null)
    {
        if (OwnNetworking(old)) yield break;
        var primary = PrimaryNetwork(old);
        foreach (var (net, ep) in Networks(old))
            if (net != primary) yield return (net, Endpoint(old, ep, name, aliasFrom));
    }

    private static bool Same(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(Empty(a), Empty(b));

    // null, "", [] and {} all mean "not set"
    private static JsonNode? Empty(JsonNode? n) => n switch
    {
        JsonArray { Count: 0 } or JsonObject { Count: 0 } => null,
        JsonValue v when v.TryGetValue<string>(out var s) && s.Length == 0 => null,
        _ => n,
    };

    // only the aliases, without the old container's short id, which Docker adds by itself (its addresses would clash)
    private static JsonObject Endpoint(JsonObject old, JsonNode? ep, string name, string? aliasFrom)
    {
        var id = Str(old["Id"]);
        var shortId = id.Length >= 12 ? id[..12] : id;
        var aliases = (ep?["Aliases"] as JsonArray ?? []).Select(Str).Where(a => a.Length > 0 && a != shortId)
            .Select(a => a == aliasFrom ? name : a).Distinct().ToArray();
        return aliases.Length == 0 ? [] : new JsonObject { ["Aliases"] = new JsonArray(aliases.Select(a => (JsonNode?)a).ToArray()) };
    }
}
