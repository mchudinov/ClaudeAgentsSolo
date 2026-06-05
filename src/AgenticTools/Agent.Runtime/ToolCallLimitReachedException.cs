namespace Agent.Runtime;

/// <summary>
/// Thrown when a local tool invocation would push a run's tool-call count past its configured
/// maximum. The runtime layer defines the exception type (so a host's run loop can catch a single
/// well-known type alongside <see cref="HardCapReachedException"/>); the host's tool adapter does
/// the counting and raises it.
/// </summary>
public sealed class ToolCallLimitReachedException : Exception
{
    public ToolCallLimitReachedException(int toolCalls, int cap)
        : base($"Tool-call limit of {cap} reached (tool calls used: {toolCalls}).")
    {
        ToolCallsUsed = toolCalls;
        Cap = cap;
    }

    /// <summary>The tool-call count that tripped the limit.</summary>
    public int ToolCallsUsed { get; }

    /// <summary>The configured maximum number of local tool calls.</summary>
    public int Cap { get; }
}
