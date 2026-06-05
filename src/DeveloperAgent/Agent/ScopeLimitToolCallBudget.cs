namespace DeveloperAgent.Agent;

/// <summary>
/// The developer-agent's <see cref="IToolCallBudget"/>: caps local tool invocations at
/// <c>ScopeLimitOptions.MaxToolCalls</c>, counting against the run's
/// <see cref="AgentRunState.ToolCallsUsed"/>. Throws <see cref="ToolCallLimitReachedException"/>
/// (Agent.Runtime) when the cap is reached so <see cref="AnthropicAgentRunner"/> can surface an
/// <see cref="AgentRunOutcome.ToolCallLimitReached"/> outcome.
/// </summary>
/// <remarks>
/// One instance per run, shared by every <see cref="Agent.Tools.MafToolAdapter"/>; since the runner
/// disables parallel tool calls the gate/increment are never concurrent.
/// </remarks>
internal sealed class ScopeLimitToolCallBudget : IToolCallBudget
{
    private readonly AgentRunState _session;
    private readonly int _maxToolCalls;

    public ScopeLimitToolCallBudget(AgentRunState session, int maxToolCalls)
    {
        _session = session;
        _maxToolCalls = maxToolCalls;
    }

    public void ThrowIfExhausted()
    {
        if (_session.ToolCallsUsed >= _maxToolCalls)
            throw new ToolCallLimitReachedException(_session.ToolCallsUsed, _maxToolCalls);
    }

    public void Record() => _session.ToolCallsUsed++;
}
