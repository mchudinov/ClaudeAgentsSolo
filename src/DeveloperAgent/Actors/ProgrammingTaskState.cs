using DeveloperAgent.Lifecycle;

namespace DeveloperAgent.Actors;

/// <summary>
/// Persistent state owned by <see cref="ProgrammingTaskActor"/> — the per-item
/// snapshot kept in the Dapr actor state store.
/// </summary>
/// <remarks>
/// Distinct from <see cref="TaskState"/> (the lifecycle loop's in-process DTO).
/// The lifecycle loop's <c>ITaskStateStore</c> is swapped to a Dapr-actor-backed
/// implementation in Step-11; that step owns the mapping between the two records.
/// </remarks>
public sealed record ProgrammingTaskState(
    string ProjectItemId,
    string? AgentId,
    TaskPhase Phase,
    string? BranchName,
    int? PullRequestNumber,
    int RetryCount,
    ApprovalStatus ApprovalStatus)
{
    /// <summary>
    /// Returns an empty state record for the given project item, used when
    /// the actor is activated and no prior state exists in the store.
    /// </summary>
    public static ProgrammingTaskState Empty(string projectItemId) => new(
        ProjectItemId: projectItemId,
        AgentId: null,
        Phase: TaskPhase.Acquired,
        BranchName: null,
        PullRequestNumber: null,
        RetryCount: 0,
        ApprovalStatus: ApprovalStatus.None);
}

/// <summary>
/// Review approval status tracked by the actor — orthogonal to <see cref="TaskPhase"/>
/// because a PR can be in <see cref="TaskPhase.AwaitingReview"/> and either
/// <see cref="WaitingForReview"/>, <see cref="Approved"/>, or <see cref="ChangesRequested"/>.
/// </summary>
public enum ApprovalStatus
{
    /// <summary>No PR opened yet, or the agent has not yet handed the PR over for review.</summary>
    None,

    /// <summary>PR is open and the agent is polling for a verdict.</summary>
    WaitingForReview,

    /// <summary>Reviewer approved; the workflow may merge / move to Done.</summary>
    Approved,

    /// <summary>Reviewer requested changes; the agent must continue on the same branch.</summary>
    ChangesRequested
}
