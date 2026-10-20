using System.Net.Security;
using System.Security.Cryptography;
using Grpc.Net.Client;

namespace Dockmaster.Agent;

/// <summary>The gRPC channel to the server, with the server certificate pinned to DOCKMASTER_SERVERFINGERPRINT.</summary>
internal static class ServerChannel
{
    // deploy payloads carry whole compose files, env files and secrets
    private const int MaxMessageSize = 64 * 1024 * 1024;

    public static GrpcChannel Create(AgentOptions opts, ILogger log)
    {
        var handler = new SocketsHttpHandler
        {
            KeepAlivePingDelay = TimeSpan.FromSeconds(20),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            EnableMultipleHttp2Connections = false,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
        };
        var fp = opts.ServerFingerprint.Replace(":", "").Replace(" ", "").ToUpperInvariant();
        if (fp.Length == 0) log.LogWarning("DOCKMASTER_SERVERFINGERPRINT is not set: the server certificate is not checked and the agent token could be intercepted");
        handler.SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, cert, _, errors) =>
            {
                if (cert == null) return false;
                if (fp.Length == 0) return true; // pinning disabled: warned about at start
                var actual = Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
                var ok = string.Equals(actual, fp, StringComparison.OrdinalIgnoreCase);
                if (!ok) log.LogError("server certificate fingerprint mismatch: got {Actual}, expected {Expected}", actual, fp);
                return ok;
            }
        };
        return GrpcChannel.ForAddress(opts.ServerUrl, new GrpcChannelOptions { HttpHandler = handler, MaxReceiveMessageSize = MaxMessageSize });
    }
}
