using Agent.Runtime;

namespace Agent.Runtime.Tests;

public sealed class CapExceptionsTests
{
    [Fact]
    public void HardCapReachedException_carries_turns_and_cap()
    {
        var ex = new HardCapReachedException(turns: 41, cap: 40);

        ex.TurnsUsed.Should().Be(41);
        ex.Cap.Should().Be(40);
        ex.Message.Should().Contain("40").And.Contain("41");
    }

    [Fact]
    public void ToolCallLimitReachedException_carries_tool_calls_and_cap()
    {
        var ex = new ToolCallLimitReachedException(toolCalls: 200, cap: 200);

        ex.ToolCallsUsed.Should().Be(200);
        ex.Cap.Should().Be(200);
        ex.Message.Should().Contain("200");
    }
}
