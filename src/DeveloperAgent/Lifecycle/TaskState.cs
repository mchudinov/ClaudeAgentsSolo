namespace DeveloperAgent.Lifecycle;

/// <summary>
/// Cursor state for the active task. Exposed via <c>/info</c> and tracked in
/// <see cref="ITaskStateStore"/> so logs and diagnostics can see what the agent is doing.
/// </summary>
public sealed record TaskState(
    string ProjectItemId,
    int IssueNumber,
    string Title,
    TaskPhase Phase,
    string? BranchName,
    int? PullRequestNumber,
    string? LastError,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? PullRequestOpenedAtUtc,
    DateTimeOffset? LastReviewPolledAtUtc);
