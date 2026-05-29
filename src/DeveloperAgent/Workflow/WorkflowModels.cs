using DeveloperAgent.Agent;
using DeveloperAgent.GitHub;

namespace DeveloperAgent.Workflow;

// ── Top-level workflow I/O ───────────────────────────────────────────────────

/// <summary>Input passed to every workflow instance and forwarded to each activity.</summary>
/// <remarks>
/// The five primary positional members (<c>ProjectItemId</c>..<c>BodyMarkdown</c>) describe
/// the GitHub item under work. The five named-init properties below are dispatcher-supplied
/// metadata: retry-policy values and recovery flags.
/// </remarks>
public sealed record TaskInput(string ProjectItemId, string ContentNodeId, int ContentNumber, string Title, string BodyMarkdown = "")
{
    /// <summary>
    /// Maximum attempts (including the first) the workflow engine will make per activity.
    /// Populated by <see cref="DeveloperAgent.Lifecycle.AgentLifecycleService"/> from
    /// <see cref="DeveloperAgent.Configuration.ScopeLimitOptions.MaxRetryCount"/> (the
    /// scope-limit policy's retry cap). Default of 3 keeps existing tests/round-trips
    /// deterministic when omitted.
    /// </summary>
    public int MaxRetryAttempts { get; init; } = 3;

    /// <summary>
    /// Delay (seconds) between the first and second activity attempt. Subsequent retries
    /// use exponential back-off (coefficient 2.0, capped at 60s).
    /// Populated from <see cref="DeveloperAgent.Configuration.AgentOptions.FirstRetryIntervalSeconds"/>.
    /// </summary>
    public int FirstRetryIntervalSeconds { get; init; } = 2;

    /// <summary>
    /// Recovery fast-path marker: when <c>true</c>, the workflow skips Acquire/Branch/Plan/CreatePR
    /// and jumps straight to <see cref="Activities.DoneActivity"/> with success=true. Used by
    /// <see cref="DeveloperAgent.Lifecycle.AgentLifecycleService"/> at startup when an item was
    /// already in InReview with a merged PR before the agent restarted.
    /// </summary>
    public bool RecoveryAlreadyMerged { get; init; }

    /// <summary>PR number known at recovery time; passed to <see cref="Activities.DoneActivity"/>.</summary>
    public int? RecoveryPullRequestNumber { get; init; }

    /// <summary>Branch name known at recovery time; passed to <see cref="Activities.DoneActivity"/>.</summary>
    public string RecoveryBranchName { get; init; } = string.Empty;
}

/// <summary>Final result produced by <see cref="DeveloperTaskWorkflow"/>.</summary>
/// <param name="Outcome">One of "Done", "Failed", or "Cancelled".</param>
public sealed record TaskResult(string Outcome);

/// <summary>
/// Empty payload for review-loop external events (<c>ChangesRequested</c>, <c>Merged</c>).
/// The event name itself carries the signal — the body exists only so the SDK can
/// deserialize a typed payload via <c>WaitForExternalEventAsync&lt;ReviewEventPayload&gt;</c>.
/// </summary>
public sealed record ReviewEventPayload(int PullRequestNumber);

// ── Per-activity input records ────────────────────────────────────────────────

/// <summary>Input for <see cref="Activities.AcquireTaskActivity"/>.</summary>
public sealed record AcquireTaskActivityInput(
    string ProjectItemId,
    string ContentNodeId,
    int ContentNumber,
    string Title,
    string BodyMarkdown);

/// <summary>Input for <see cref="Activities.CreateBranchActivity"/>.</summary>
public sealed record CreateBranchActivityInput(
    string ProjectItemId,
    string ContentNodeId,
    int ContentNumber,
    string Title,
    string BodyMarkdown,
    string BranchName);

/// <summary>Input for <see cref="Activities.PlanActivity"/>.</summary>
public sealed record PlanActivityInput(
    string ProjectItemId,
    string ContentNodeId,
    int ContentNumber,
    string Title,
    string BodyMarkdown,
    string BranchName,
    string WorkspacePath,
    string DefaultBranch);

/// <summary>Input for <see cref="Activities.ModifyCodeActivity"/>.</summary>
public sealed record ModifyCodeActivityInput(
    string ProjectItemId,
    string ContentNodeId,
    int ContentNumber,
    string Title,
    string BodyMarkdown,
    string BranchName,
    string WorkspacePath,
    string DefaultBranch,
    int PullRequestNumber,
    string PriorReviewFeedback,
    DateTimeOffset LastReviewPolledAtUtc);

/// <summary>Input for <see cref="Activities.CreatePullRequestActivity"/>.</summary>
public sealed record CreatePullRequestActivityInput(
    string ProjectItemId,
    string ContentNodeId,
    int ContentNumber,
    string Title,
    string BranchName);

/// <summary>Input for <see cref="Activities.WaitForReviewActivity"/>.</summary>
public sealed record WaitForReviewActivityInput(
    string ProjectItemId,
    int PullRequestNumber);

/// <summary>Input for <see cref="Activities.DoneActivity"/>.</summary>
public sealed record DoneActivityInput(
    string ProjectItemId,
    string ContentNodeId,
    string WorkspacePath,
    string BranchName,
    string DefaultBranch,
    int? PullRequestNumber,
    bool Success,
    long ToolCallsUsed);

// ── Per-activity result records ───────────────────────────────────────────────

/// <summary>Result of <see cref="Activities.AcquireTaskActivity"/>.</summary>
public sealed record AcquireTaskResult(string BranchName);

/// <summary>Result of <see cref="Activities.CreateBranchActivity"/>.</summary>
public sealed record CreateBranchResult(string WorkspacePath, string DefaultBranch);

/// <summary>Result of <see cref="Activities.PlanActivity"/>.</summary>
public sealed record PlanResult(AgentRunOutcome Outcome, int? PullRequestNumber, long ToolCallsUsed, string? TerminationReason);

/// <summary>Result of <see cref="Activities.ModifyCodeActivity"/>.</summary>
public sealed record ModifyCodeResult(AgentRunOutcome Outcome, long ToolCallsUsed, string? TerminationReason);

/// <summary>Result of <see cref="Activities.CreatePullRequestActivity"/>.</summary>
public sealed record CreatePullRequestResult(int PullRequestNumber);

/// <summary>Result of <see cref="Activities.WaitForReviewActivity"/>.</summary>
public sealed record WaitForReviewResult(PullRequestReviewState ReviewState, bool Merged, bool ChecksGreen, string? FeedbackMarkdown, DateTimeOffset PolledAtUtc);
