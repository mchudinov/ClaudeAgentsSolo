using Dapr.Workflow;
using DeveloperAgent.GitHub;
using DeveloperAgent.Lifecycle;
using DeveloperAgent.Observability;
using Agent.Workspace;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Completes the task lifecycle: moves the GitHub item to Done, updates task state,
/// releases the workspace, and records metrics.
/// Called on both the success path and failure path (controlled by <see cref="DoneActivityInput.Success"/>).
/// </summary>
public sealed class DoneActivity : WorkflowActivity<DoneActivityInput, object?>
{
    private readonly ILogger<DoneActivity> _logger;
    private readonly IGitHubProjectService _github;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ITaskStateStore _taskStateStore;
    private readonly AgentMetrics _metrics;

    /// <summary>
    /// Return-reason comment posted when a closed-without-merge PR sends the item to Backlog. Kept
    /// distinct from the triage-reject and already-resolved reasons, and actionable (Backlog is
    /// write-only, so the comment is the human's only signal that the item was parked and how to revive it).
    /// </summary>
    private const string ClosedPrParkComment =
        "**Returned to Backlog: the pull request was closed without being merged.** " +
        "No reviewer remains to act on this item, so the agent parked it for a human to re-triage. " +
        "If the work is still wanted, move the item back to **Ready** to re-queue it.";

    public DoneActivity(
        ILogger<DoneActivity> logger,
        IGitHubProjectService github,
        IWorkspaceManager workspaceManager,
        ITaskStateStore taskStateStore,
        AgentMetrics metrics)
    {
        _logger = logger;
        _github = github;
        _workspaceManager = workspaceManager;
        _taskStateStore = taskStateStore;
        _metrics = metrics;
    }

    public override async Task<object?> RunAsync(WorkflowActivityContext context, DoneActivityInput input)
    {
        var ct = CancellationToken.None;

        if (input.Success)
        {
            await _github.MoveItemAsync(
                input.ProjectItemId,
                ProjectState.InReview,
                ProjectState.Done,
                ct);

            var current = _taskStateStore.Current;
            if (current is not null)
            {
                _taskStateStore.Set(current with
                {
                    Phase = TaskPhase.Done,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });
            }

            _logger.LogInformation(
                "[{Activity}] Task done. item={ItemId}",
                nameof(DoneActivity), input.ProjectItemId);
        }
        else if (input.PullRequestNumber is null)
        {
            // Failure with no PR opened: the workflow could not get the work past PlanActivity.
            // Step-53: park the item InProgress → Backlog instead of releasing it back to Ready.
            // Releasing to Ready let the poller immediately re-grab the item, producing an infinite
            // redispatch loop on any item the agent could not implement on the first round. Backlog
            // is a write-only holding column the poller never reads, so a failed item rests there
            // until a human moves it back to Ready. DoneActivity is the single owner of this
            // transition (PlanActivity no longer moves the item — see Step-53). The transition is
            // InProgress → Backlog because AcquireTaskActivity already moved it Ready → InProgress.
            try
            {
                await _github.MoveItemAsync(
                    input.ProjectItemId,
                    ProjectState.InProgress,
                    ProjectState.Backlog,
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[{Activity}] Failed to park item in Backlog. item={ItemId}",
                    nameof(DoneActivity), input.ProjectItemId);
            }

            _logger.LogWarning(
                "[{Activity}] Task finished with failure (no PR). Item parked in Backlog. item={ItemId}",
                nameof(DoneActivity), input.ProjectItemId);
        }
        else if (input.ParkInBacklog)
        {
            // Failure with a PR that was closed without merging. There is no reviewer left to act,
            // so park the item InReview → Backlog (mirroring the no-PR Step-53 parking, from the
            // InReview column) for a human to re-triage — rather than leaving it orphaned in
            // In Review, where it would be re-polled on every agent restart.
            //
            // Always record WHY the item was returned: this is the only live Backlog-parking path that
            // otherwise moves the item with no comment, so a maintainer would see it land in Backlog
            // with no explanation. Best-effort — a comment failure must not block the park itself.
            try
            {
                await _github.AddItemCommentAsync(input.ContentNodeId, ClosedPrParkComment, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[{Activity}] Failed to post closed-PR park comment. item={ItemId}",
                    nameof(DoneActivity), input.ProjectItemId);
            }

            try
            {
                await _github.MoveItemAsync(
                    input.ProjectItemId,
                    ProjectState.InReview,
                    ProjectState.Backlog,
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[{Activity}] Failed to park closed-PR item in Backlog. item={ItemId}",
                    nameof(DoneActivity), input.ProjectItemId);
            }

            _logger.LogWarning(
                "[{Activity}] PR#{PrNumber} was closed without merging. Item parked in Backlog. item={ItemId}",
                nameof(DoneActivity), input.PullRequestNumber, input.ProjectItemId);
        }
        else
        {
            // Failure with a PR open: leave the item in InReview so the human reviewer can
            // take over. The PR remains; no state transition.
            _logger.LogWarning(
                "[{Activity}] Task finished with failure. PR#{PrNumber} left open in InReview. item={ItemId}",
                nameof(DoneActivity), input.PullRequestNumber, input.ProjectItemId);
        }

        // Release workspace if a path was set
        if (!string.IsNullOrEmpty(input.WorkspacePath))
        {
            try
            {
                var ws = new TaskWorkspace(
                    ProjectItemId: input.ProjectItemId,
                    BranchName: input.BranchName,
                    RepoRoot: input.WorkspacePath,
                    DefaultBranch: input.DefaultBranch);

                await _workspaceManager.ReleaseAsync(ws, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[{Activity}] Failed to release workspace. item={ItemId} path={Path}",
                    nameof(DoneActivity), input.ProjectItemId, input.WorkspacePath);
            }
        }

        _metrics.RecordTaskTerminated(success: input.Success, toolCalls: input.ToolCallsUsed);

        return null;
    }
}
