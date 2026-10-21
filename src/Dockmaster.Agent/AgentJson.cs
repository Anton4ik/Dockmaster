using System.Text.Json;

namespace Dockmaster.Agent;

/// <summary>JSON settings of the payloads the agent sends to the server and the UI.</summary>
internal static class AgentJson
{
    /// <summary>camelCase names, the way the server and the UI read them.</summary>
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}
