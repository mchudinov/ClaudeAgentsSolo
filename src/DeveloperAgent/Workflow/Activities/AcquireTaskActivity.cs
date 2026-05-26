using Dapr.Workflow;
using DeveloperAgent.GitHub;
using DeveloperAgent.Lifecycle;
using DeveloperAgent.Workspace;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Acquires the GitHub project item and transitions it from Ready to InProgress.
/// Sets initial <see cref="TaskState"/> with <see cref="TaskPhase.Acquired"/>.
/// Returns <see cref="AcquireTaskResult"/> containing the generated branch name.
/// </summary>
public sealed class AcquireTaskActivity : WorkflowActivity<AcquireTaskActivityInput, AcquireTaskResult>
{
    private readonly ILogger<AcquireTaskActivity> _logger;
    private readonly IGitHubProjectService _github;
    private readonly ITaskStateStore _taskStateStore;

    public AcquireTaskActivity(
        ILogger<AcquireTaskActivity> logger,
        IGitHubProjectService github,
        ITaskStateStore taskStateStore)
    {
        _logger = logger;
        _github = github;
        _taskStateStore = taskStateStore;
    }

    public override async Task<AcquireTaskResult> RunAsync(
        WorkflowActivityContext context, AcquireTaskActivityInput input)
    {
        var ct = CancellationToken.None;

        await _github.MoveItemAsync(
            input.ProjectItemId,
            ProjectState.Ready,
            ProjectState.InProgress,
            ct);

        var branchName = BranchName.ForTask(threadId: null, input.Title);
        var now = DateTimeOffset.UtcNow;

        var state = new TaskState(
            ProjectItemId: input.ProjectItemId,
            IssueNumber: input.ContentNumber,
            Title: input.Title,
            Phase: TaskPhase.Acquired,
            BranchName: null,
            PullRequestNumber: null,
            LastError: null,
            StartedAtUtc: now,
            UpdatedAtUtc: now,
            PullRequestOpenedAtUtc: null,
            LastReviewPolledAtUtc: null);

        _taskStateStore.Set(state);

        _logger.LogInformation(
            "[{Activity}] item={ItemId} issueNumber={IssueNumber} phase={Phase}",
            nameof(AcquireTaskActivity), input.ProjectItemId, input.ContentNumber, TaskPhase.Acquired);

        return new AcquireTaskResult(branchName);
    }
}
