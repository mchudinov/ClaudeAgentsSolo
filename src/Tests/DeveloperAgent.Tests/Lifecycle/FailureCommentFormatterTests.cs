using DeveloperAgent.Agent;
using DeveloperAgent.GitHub;
using DeveloperAgent.Lifecycle;

namespace DeveloperAgent.Tests.Lifecycle;

public sealed class FailureCommentFormatterTests
{
    private static AgentRunResult MakeResult(
        AgentRunOutcome outcome,
        string? reason = null) =>
        new(outcome, PullRequest: null, TurnsUsed: 1, ToolCallsUsed: 0, TerminationReason: reason);

    [Fact]
    public void HardCapReached_produces_turn_cap_message()
    {
        var result = MakeResult(AgentRunOutcome.HardCapReached);

        var comment = FailureCommentFormatter.Format(result);

        comment.Should().Contain("turn cap");
    }

    [Fact]
    public void SandboxViolation_produces_sandbox_message()
    {
        var result = MakeResult(AgentRunOutcome.SandboxViolation);

        var comment = FailureCommentFormatter.Format(result);

        comment.Should().Contain("Sandbox violation");
    }

    [Fact]
    public void ApiError_produces_api_error_message()
    {
        var result = MakeResult(AgentRunOutcome.ApiError);

        var comment = FailureCommentFormatter.Format(result);

        comment.Should().Contain("API error");
    }

    [Fact]
    public void Cancelled_produces_cancelled_message()
    {
        var result = MakeResult(AgentRunOutcome.Cancelled);

        var comment = FailureCommentFormatter.Format(result);

        comment.Should().Contain("cancelled");
    }

    [Fact]
    public void SandboxViolation_message_does_NOT_contain_TerminationReason()
    {
        var offendingCommand = "rm -rf /etc";
        var result = MakeResult(AgentRunOutcome.SandboxViolation, reason: offendingCommand);

        var comment = FailureCommentFormatter.Format(result);

        comment.Should().NotContain("rm -rf");
        comment.Should().NotContain("/etc");
        comment.Should().NotContain(offendingCommand);
    }

    [Fact]
    public void HardCapReached_with_TerminationReason_includes_reason()
    {
        var result = MakeResult(AgentRunOutcome.HardCapReached, reason: "Reached 40 turns");

        var comment = FailureCommentFormatter.Format(result);

        comment.Should().Contain("Reached 40 turns");
    }

    [Fact]
    public void ApiError_with_TerminationReason_includes_reason()
    {
        var result = MakeResult(AgentRunOutcome.ApiError, reason: "Rate limit exceeded");

        var comment = FailureCommentFormatter.Format(result);

        comment.Should().Contain("Rate limit exceeded");
    }

    [Fact]
    public void Cancelled_with_TerminationReason_includes_reason()
    {
        var result = MakeResult(AgentRunOutcome.Cancelled, reason: "Operator stop requested");

        var comment = FailureCommentFormatter.Format(result);

        comment.Should().Contain("Operator stop requested");
    }

    [Fact]
    public void SandboxViolation_null_TerminationReason_produces_clean_message()
    {
        var result = MakeResult(AgentRunOutcome.SandboxViolation, reason: null);

        var comment = FailureCommentFormatter.Format(result);

        comment.Should().Contain("Sandbox violation");
        comment.Should().NotContain("Reason:");
    }
}
