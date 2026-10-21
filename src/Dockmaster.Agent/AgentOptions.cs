namespace Dockmaster.Agent;

/// <summary>Agent settings, from DOCKMASTER_* environment variables (DOCKMASTER_SERVERURL, DOCKMASTER_TOKEN...).</summary>
public sealed class AgentOptions
{
    /// <summary>Server agent endpoint, e.g. https://nas.example.com:9443</summary>
    public string ServerUrl { get; set; } = "";
    /// <summary>Agent token issued by the server when the agent was added in the UI.</summary>
    public string Token { get; set; } = "";
    /// <summary>SHA-256 fingerprint of the server certificate (hex, case-insensitive, colons optional). Empty = trust any (not recommended).</summary>
    public string ServerFingerprint { get; set; } = "";
    /// <summary>Where project files (compose + .env) are stored.</summary>
    public string DataDir { get; set; } = "/var/lib/dockmaster";
    /// <summary>Docker engine endpoint, for the API client and as DOCKER_HOST of the docker CLI.</summary>
    public string DockerSocket { get; set; } = "unix:///var/run/docker.sock";
    /// <summary>The docker CLI (with the compose plugin) to run.</summary>
    public string DockerCli { get; set; } = "docker";
    /// <summary>Seconds between heartbeats when the server does not say (its welcome message usually does).</summary>
    public int HeartbeatSeconds { get; set; } = 5;
    /// <summary>Optional: file that holds the real host name when running inside a container (mount /etc/hostname there).</summary>
    public string HostnameFile { get; set; } = "/etc/host_hostname";
    /// <summary>Host directory with the docker login used to pull a newer agent image (docker login as root writes here).</summary>
    public string HostDockerConfig { get; set; } = "/root/.docker";
}
