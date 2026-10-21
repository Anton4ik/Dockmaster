using System.Text.Json;

namespace Dockmaster.Agent.Docker;

/// <summary>
/// One container for its page in the UI: what `docker inspect` says, trimmed to what a person looks at (config, env,
/// state with the health log, networks, ports, mounts, limits) and in plain JSON, so the server and the UI do not
/// depend on the Engine API's spelling.
/// </summary>
public static class ContainerDetails
{
    public static string ToJson(ContainerInspectResponse c, ImageInspectResponse? image)
    {
        var h = c.HostConfig;
        var s = c.State;
        var imageEnv = new HashSet<string>(image?.Config?.Env ?? []);
        return JsonSerializer.Serialize(new
        {
            id = c.ID,
            name = c.Name.TrimStart('/'),
            image = c.Config.Image,
            imageId = c.Image,
            created = c.Created,
            platform = c.Platform,
            command = string.Join(' ', new[] { c.Path }.Concat(c.Args ?? [])),
            entrypoint = c.Config.Entrypoint ?? [],
            cmd = c.Config.Cmd ?? [],
            workingDir = c.Config.WorkingDir,
            user = c.Config.User,
            hostname = c.Config.Hostname,
            state = new
            {
                status = s.Status,
                running = s.Running,
                oomKilled = s.OOMKilled,
                exitCode = s.ExitCode,
                error = s.Error,
                startedAt = s.StartedAt,
                finishedAt = s.FinishedAt,
                restartCount = c.RestartCount,
                health = s.Health == null ? null : new
                {
                    status = s.Health.Status,
                    failingStreak = s.Health.FailingStreak,
                    log = (s.Health.Log ?? []).Select(l => new { start = l.Start, end = l.End, exitCode = l.ExitCode, output = l.Output?.Trim() }),
                },
            },
            healthcheck = c.Config.Healthcheck?.Test is { Count: > 0 } t ? new
            {
                test = t,
                // Docker gives nanoseconds
                interval = c.Config.Healthcheck.Interval / 1e9,
                timeout = c.Config.Healthcheck.Timeout / 1e9,
                retries = c.Config.Healthcheck.Retries,
                startPeriod = c.Config.Healthcheck.StartPeriod / 1e9,
            } : null,
            // the image's own variables (PATH, DOTNET_…) are marked, so the ones set for this container stand out
            env = (c.Config.Env ?? []).Select(e =>
            {
                var i = e.IndexOf('=');
                return new { name = i < 0 ? e : e[..i], value = i < 0 ? "" : e[(i + 1)..], fromImage = imageEnv.Contains(e) };
            }),
            labels = c.Config.Labels ?? new Dictionary<string, string>(),
            restartPolicy = DockerService.RestartPolicyName(h?.RestartPolicy),
            maxRetries = h?.RestartPolicy?.MaximumRetryCount,
            memoryLimit = h?.Memory ?? 0,
            memoryReservation = h?.MemoryReservation ?? 0,
            cpus = (h?.NanoCPUs ?? 0) / 1e9,
            cpuShares = h?.CPUShares ?? 0,
            privileged = h?.Privileged ?? false,
            networkMode = h?.NetworkMode,
            logDriver = h?.LogConfig?.Type,
            networks = (c.NetworkSettings?.Networks ?? []).Select(n => new
            {
                name = n.Key,
                ip = n.Value.IPAddress,
                prefix = n.Value.IPPrefixLen,
                gateway = n.Value.Gateway,
                mac = n.Value.MacAddress,
                aliases = (n.Value.Aliases ?? []).Where(a => a != c.ID[..12]),
            }),
            ports = (c.NetworkSettings?.Ports ?? []).Select(p => new
            {
                container = p.Key,
                published = (p.Value ?? []).Select(b => $"{(string.IsNullOrEmpty(b.HostIP) || b.HostIP is "0.0.0.0" or "::" ? "" : b.HostIP + ":")}{b.HostPort}").Distinct(),
            }),
            mounts = (c.Mounts ?? []).Select(m => new { type = m.Type, name = m.Name, source = m.Source, destination = m.Destination, mode = m.Mode, rw = m.RW }),
        }, AgentJson.Web);
    }
}
