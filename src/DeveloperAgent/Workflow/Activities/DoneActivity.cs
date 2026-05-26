using Dapr.Workflow;
using DeveloperAgent.GitHub;
using DeveloperAgent.Lifecycle;
using DeveloperAgent.Observability;
using DeveloperAgent.Workspace;
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
        else
        {
            _logger.LogWarning(
                "[{Activity}] Task finished with failure. item={ItemId}",
                nameof(DoneActivity), input.ProjectItemId);
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
