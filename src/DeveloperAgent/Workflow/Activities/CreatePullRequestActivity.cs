using Dapr.Workflow;
using DeveloperAgent.GitHub;
using DeveloperAgent.Lifecycle;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Moves the GitHub project item from InProgress to InReview and
/// updates task state to <see cref="TaskPhase.AwaitingReview"/>.
/// The pull request was already created by the agent during <see cref="PlanActivity"/>;
/// this activity records the PR number in task state and transitions the board column.
/// Returns <see cref="CreatePullRequestResult"/> with the pull request number taken from
/// <see cref="ITaskStateStore.Current"/> (set by the agent via tool calls).
/// </summary>
public sealed class CreatePullRequestActivity : WorkflowActivity<CreatePullRequestActivityInput, CreatePullRequestResult>
{
    private readonly ILogger<CreatePullRequestActivity> _logger;
    private readonly IGitHubProjectService _github;
    private readonly ITaskStateStore _taskStateStore;

    public CreatePullRequestActivity(
        ILogger<CreatePullRequestActivity> logger,
        IGitHubProjectService github,
        ITaskStateStore taskStateStore)
    {
        _logger = logger;
        _github = github;
        _taskStateStore = taskStateStore;
    }

    public override async Task<CreatePullRequestResult> RunAsync(
        WorkflowActivityContext context, CreatePullRequestActivityInput input)
    {
        var ct = CancellationToken.None;
        var now = DateTimeOffset.UtcNow;

        // The PR number was set in task state by the agent's tool calls during PlanActivity.
        var prNumber = _taskStateStore.Current?.PullRequestNumber ?? 0;

        var current = _taskStateStore.Current;
        if (current is not null)
        {
            _taskStateStore.Set(current with
            {
                Phase = TaskPhase.PullRequestOpen,
                PullRequestNumber = prNumber,
                PullRequestOpenedAtUtc = now,
                LastReviewPolledAtUtc = now,
                UpdatedAtUtc = now
            });
        }

        await _github.MoveItemAsync(
            input.ProjectItemId,
            ProjectState.InProgress,
            ProjectState.InReview,
            ct);

        if (_taskStateStore.Current is not null)
        {
            _taskStateStore.Set(_taskStateStore.Current with
            {
                Phase = TaskPhase.AwaitingReview,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
        }

        _logger.LogInformation(
            "[{Activity}] item={ItemId} prNumber={PrNumber} phase={Phase}",
            nameof(CreatePullRequestActivity), input.ProjectItemId, prNumber, TaskPhase.AwaitingReview);

        return new CreatePullRequestResult(prNumber);
    }
}
