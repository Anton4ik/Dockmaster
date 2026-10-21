namespace Dockmaster.Server.Api;

/// <param name="MeshMtu">a tunnel MTU set by hand for every host; 0 = each host's from its measured paths (Tune speed on the Network page)</param>
/// <param name="MeshTcpTuning">the mesh hosts get BBR and bigger TCP buffers</param>
public sealed record SettingsDto(string AgentPublicUrl, string AgentImage, string Fingerprint, string FingerprintPretty, string MeshSubnet, int MeshPort, bool MeshInUse,
    int MeshMtu = 0, bool MeshTcpTuning = false);
/// <param name="MeshMtu">null leaves it; 0 = automatic</param>
/// <param name="MeshTcpTuning">null leaves it</param>
public sealed record SaveSettingsRequest(string AgentPublicUrl, string AgentImage, string? MeshSubnet = null, int? MeshPort = null, int? MeshMtu = null, bool? MeshTcpTuning = null);
