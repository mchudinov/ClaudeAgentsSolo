using DeveloperAgent.Agent;

namespace DeveloperAgent.Tests.Agent;

/// <summary>
/// Unit tests for the host's <see cref="ScopeLimitToolCallBudget"/> — the developer-agent's
/// <see cref="IToolCallBudget"/> (Agent.Tools seam). Locks the gate/increment semantics the
/// MafToolAdapter relies on: <see cref="ScopeLimitToolCallBudget.ThrowIfExhausted"/> throws
/// <see cref="ToolCallLimitReachedException"/> at (not before) the cap, and
/// <see cref="ScopeLimitToolCallBudget.Record"/> increments the run's tool-call count.
/// </summary>
public sealed class ScopeLimitToolCallBudgetTests
{
    [Fact]
    public void ThrowIfExhausted_does_not_throw_below_cap()
    {
        var session = new AgentRunState { ToolCallsUsed = 1 };
        var budget = new ScopeLimitToolCallBudget(session, maxToolCalls: 2);

        var act = budget.ThrowIfExhausted;

        act.Should().NotThrow();
    }

    [Fact]
    public void ThrowIfExhausted_throws_at_cap_carrying_count_and_cap()
    {
        var session = new AgentRunState { ToolCallsUsed = 2 };
        var budget = new ScopeLimitToolCallBudget(session, maxToolCalls: 2);

        var act = budget.ThrowIfExhausted;

        var ex = act.Should().Throw<ToolCallLimitReachedException>().Which;
        ex.Cap.Should().Be(2);
        ex.ToolCallsUsed.Should().Be(2);
    }

    [Fact]
    public void Record_increments_the_run_tool_call_count()
    {
        var session = new AgentRunState();
        var budget = new ScopeLimitToolCallBudget(session, maxToolCalls: 5);

        budget.Record();
        budget.Record();

        session.ToolCallsUsed.Should().Be(2);
    }
}
