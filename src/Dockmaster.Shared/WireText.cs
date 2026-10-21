namespace Dockmaster.Protocol;

/// <summary>Fixed texts both sides of the agent stream rely on.</summary>
public static class WireText
{
    /// <summary>What an agent answers to a command it does not know; the server reads it as "this agent is too old".</summary>
    public const string UnknownCommand = "unknown command";
}
