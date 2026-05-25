using Dapr.Actors;
using DeveloperAgent.Lifecycle;

namespace DeveloperAgent.Actors;

/// <summary>
/// One actor instance per GitHub Project item (actor id = the project item id).
/// Owns the durable per-item state — phase, branch, PR number, retry count,
/// approval status — and guarantees that only one container can claim a given
/// item at a time. See <c>docs/low_level_design_software.md</c> §Dapr Actors for
/// the canonical contract.
/// </summary>
public interface IProgrammingTaskActor : IActor
{
    /// <summary>
    /// Attempts to claim the task for <paramref name="agentId"/>. Returns
    /// <see langword="true"/> when the actor was unclaimed (and the claim now
    /// belongs to <paramref name="agentId"/>) or when the same agent is
    /// re-claiming. Returns <see langword="false"/> when a different agent
    /// already holds the claim — that is the "two containers cannot work on
    /// the same item" invariant from the LLD.
    /// </summary>
    Task<bool> TryClaimAsync(string agentId);

    /// <summary>Records the current <see cref="TaskPhase"/>.</summary>
    Task SetPhaseAsync(TaskPhase phase);

    /// <summary>Records the agent's working branch name.</summary>
    Task SaveBranchAsync(string branchName);

    /// <summary>Records the pull request number opened by the agent.</summary>
    Task SavePullRequestAsync(int pullRequestNumber);

    /// <summary>
    /// Transitions to <see cref="TaskPhase.AwaitingReview"/> and sets
    /// <see cref="ApprovalStatus"/> to <see cref="ApprovalStatus.WaitingForReview"/>.
    /// </summary>
    Task MarkWaitingForReviewAsync();

    /// <summary>Marks the PR as approved. Phase is left as-is so the workflow can drive the merge.</summary>
    Task MarkApprovedAsync();

    /// <summary>
    /// Marks the PR as needing more work and rewinds the phase to
    /// <see cref="TaskPhase.AgentRunning"/> so the workflow re-enters the agent
    /// loop on the same branch.
    /// </summary>
    Task MarkChangesRequestedAsync();

    /// <summary>
    /// Increments the persisted retry counter and returns the new value. Not
    /// idempotent — each call is a real retry event.
    /// </summary>
    Task<int> RecordRetryAsync();

    /// <summary>Returns the current persisted state for this item.</summary>
    Task<ProgrammingTaskState> GetStateAsync();
}
