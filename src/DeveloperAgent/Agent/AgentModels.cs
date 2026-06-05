using DeveloperAgent.GitHub;

namespace DeveloperAgent.Agent;

/// <summary>The outcome of a single agent run.</summary>
public enum AgentRunOutcome
{
    /// <summary>Model stopped producing tool calls — task is done.</summary>
    Completed,

    /// <summary><see cref="Configuration.ScopeLimitOptions.MaxModelTurns"/> hit; loop aborted.</summary>
    HardCapReached,

    /// <summary><see cref="Configuration.ScopeLimitOptions.MaxToolCalls"/> hit; loop aborted.</summary>
    ToolCallLimitReached,

    /// <summary><see cref="Configuration.ScopeLimitOptions.MaxExecutionTimeSeconds"/> wall-clock budget hit; run aborted.</summary>
    TimeLimitExceeded,

    /// <summary>A tool raised <see cref="global::Agent.Sandbox.SandboxViolationException"/>; task is dead.</summary>
    SandboxViolation,

    /// <summary>Unrecoverable Anthropic API error after retries.</summary>
    ApiError,

    /// <summary>The caller cancelled via <see cref="CancellationToken"/>.</summary>
    Cancelled
}

/// <summary>Input to a single agent run.</summary>
/// <param name="Item">The GitHub Project item to implement.</param>
/// <param name="Workspace">The prepared workspace for the task.</param>
/// <param name="PriorReviewFeedback">
/// Null on the first round; populated with concatenated review comments on subsequent rounds.
/// </param>
public sealed record AgentRunRequest(
    DeveloperAgent.GitHub.ProjectItem Item,
    global::Agent.Sandbox.TaskWorkspace Workspace,
    string? PriorReviewFeedback);

/// <summary>Result of a completed (or terminated) agent run.</summary>
/// <param name="Outcome">Why the run ended.</param>
/// <param name="PullRequest">Populated if the agent successfully called <c>create_pull_request</c>.</param>
/// <param name="TurnsUsed">Number of model turns consumed.</param>
/// <param name="ToolCallsUsed">Total tool invocations across all turns.</param>
/// <param name="TerminationReason">Human-readable reason, used in logs and item comments.</param>
public sealed record AgentRunResult(
    AgentRunOutcome Outcome,
    PullRequest? PullRequest,
    int TurnsUsed,
    int ToolCallsUsed,
    string? TerminationReason);
