namespace DeveloperAgent.Agent;

/// <summary>
/// Thrown by <see cref="MafToolAdapter"/> when a local tool invocation would push
/// <see cref="AgentRunState.ToolCallsUsed"/> past
/// <see cref="Configuration.ScopeLimitOptions.MaxToolCalls"/>. The runner catches this
/// and returns <see cref="AgentRunOutcome.ToolCallLimitReached"/>.
/// </summary>
internal sealed class ToolCallLimitReachedException : Exception
{
    public ToolCallLimitReachedException(int toolCalls, int cap)
        : base($"Tool-call limit of {cap} reached (tool calls used: {toolCalls}).")
    {
        ToolCallsUsed = toolCalls;
        Cap = cap;
    }

    public int ToolCallsUsed { get; }
    public int Cap { get; }
}
