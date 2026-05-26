using DeveloperAgent.Agent;
using DeveloperAgent.GitHub;

namespace DeveloperAgent.Workflow;

// ── Top-level workflow I/O ───────────────────────────────────────────────────

/// <summary>Input passed to every workflow instance and forwarded to each activity.</summary>
public sealed record TaskInput(string ProjectItemId, string ContentNodeId, int ContentNumber, string Title, string BodyMarkdown = "");

/// <summary>Final result produced by <see cref="DeveloperTaskWorkflow"/>.</summary>
/// <param name="Outcome">One of "Done", "Failed", or "Cancelled".</param>
public sealed record TaskResult(string Outcome);

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
