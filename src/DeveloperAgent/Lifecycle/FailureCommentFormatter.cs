using DeveloperAgent.Agent;

namespace DeveloperAgent.Lifecycle;

/// <summary>
/// Builds the markdown comment posted to the GitHub issue when the agent
/// terminates with a non-successful outcome.
/// </summary>
/// <remarks>
/// For <see cref="AgentRunOutcome.SandboxViolation"/>, the offending command is
/// intentionally omitted from the comment to avoid leaking sensitive path or
/// argument information. For all other outcomes, the <c>TerminationReason</c>
/// (if present) is included.
/// </remarks>
public static class FailureCommentFormatter
{
    /// <summary>Formats a failure comment for the given agent run result.</summary>
    public static string Format(AgentRunResult result)
    {
        var body = result.Outcome switch
        {
            AgentRunOutcome.HardCapReached =>
                "Agent did not finish within the turn cap.",

            AgentRunOutcome.SandboxViolation =>
                "Sandbox violation: agent attempted a disallowed operation. Releasing.",

            AgentRunOutcome.ApiError =>
                "Anthropic API error after retries.",

            AgentRunOutcome.Cancelled =>
                "Run cancelled by operator.",

            _ =>
                $"Agent run ended with unexpected outcome: {result.Outcome}."
        };

        // Append TerminationReason for all outcomes except SandboxViolation, because
        // the reason may contain the offending command line.
        if (result.Outcome != AgentRunOutcome.SandboxViolation
            && !string.IsNullOrWhiteSpace(result.TerminationReason))
        {
            body += $"\n\nReason: {result.TerminationReason}";
        }

        return body;
    }
}
