using DeveloperAgent.GitHub;

namespace DeveloperAgent.Lifecycle;

/// <summary>
/// Drives a single GitHub Project item through the per-item state machine.
/// </summary>
public interface ITaskExecutor
{
    /// <summary>
    /// Runs the full per-item state machine for <paramref name="item"/> from the
    /// <see cref="ProjectState.Ready"/> state through to a terminal outcome.
    /// </summary>
    Task<TaskExecutionResult> RunAsync(ProjectItem item, CancellationToken ct);

    /// <summary>
    /// Re-enters the review-wait loop for an item that was already in
    /// <see cref="ProjectState.InReview"/> when the agent restarted.
    /// Restores minimal <see cref="TaskState"/> then polls the PR until it reaches
    /// a terminal outcome (Done, Failed, or Cancelled).
    /// </summary>
    Task<TaskExecutionResult> ResumeReviewAsync(ProjectItem item, int pullRequestNumber, CancellationToken ct);
}
