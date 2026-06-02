using Dapr.Workflow;
using DeveloperAgent.GitHub;
using DeveloperAgent.Lifecycle;
using DeveloperAgent.Workflow;
using DeveloperAgent.Workflow.Activities;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeveloperAgent.Tests.Workflow;

/// <summary>
/// Unit tests for <see cref="CreatePullRequestActivity"/>.
/// Regression cover for the bug where the activity read the PR number from the volatile
/// task-state cache (which <see cref="PlanActivity"/> never populates with a PR number),
/// coerced the missing value to <c>0</c> via <c>?? 0</c>, and persisted that <c>0</c> —
/// tripping the actor's "Pull request number must be positive" guard. The authoritative
/// PR number now arrives via <see cref="CreatePullRequestActivityInput.PullRequestNumber"/>.
/// </summary>
public sealed class CreatePullRequestActivityTests
{
    // Mirrors the state PlanActivity leaves behind: phase + branch set, PR number still null.
    private static TaskState StateWithNullPr() => new(
        ProjectItemId: "PVTI_abc",
        IssueNumber: 42,
        Title: "Add feature X",
        Phase: TaskPhase.AgentRunning,
        BranchName: "agent/branch",
        PullRequestNumber: null,
        LastError: null,
        StartedAtUtc: DateTimeOffset.UtcNow,
        UpdatedAtUtc: DateTimeOffset.UtcNow,
        PullRequestOpenedAtUtc: null,
        LastReviewPolledAtUtc: null);

    private static CreatePullRequestActivityInput Input(int prNumber) =>
        new(ProjectItemId: "PVTI_abc", ContentNodeId: "I_node", ContentNumber: 42,
            Title: "Add feature X", BranchName: "agent/branch", PullRequestNumber: prNumber);

    private static CreatePullRequestActivity MakeActivity(IGitHubProjectService github, ITaskStateStore store) =>
        new(NullLogger<CreatePullRequestActivity>.Instance, github, store);

    [Fact]
    public async Task RunAsync_persists_the_pull_request_number_from_input_not_zero()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var store = new InMemoryTaskStateStore();
        store.Set(StateWithNullPr());

        var activity = MakeActivity(github, store);
        await activity.RunAsync(new FakeCreatePrActivityContext(), Input(123));

        store.Current!.PullRequestNumber.Should().Be(123,
            because: "the workflow supplies the real PR number via the activity input; " +
                     "coercing a missing value to 0 is what tripped the actor's positive-PR guard");
    }

    [Fact]
    public async Task RunAsync_returns_the_pull_request_number_from_input()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var store = new InMemoryTaskStateStore();
        store.Set(StateWithNullPr());

        var activity = MakeActivity(github, store);
        var result = await activity.RunAsync(new FakeCreatePrActivityContext(), Input(123));

        result!.PullRequestNumber.Should().Be(123);
    }

    [Fact]
    public async Task RunAsync_moves_item_from_InProgress_to_InReview()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var store = new InMemoryTaskStateStore();
        store.Set(StateWithNullPr());

        var activity = MakeActivity(github, store);
        await activity.RunAsync(new FakeCreatePrActivityContext(), Input(123));

        await github.Received(1).MoveItemAsync(
            "PVTI_abc", ProjectState.InProgress, ProjectState.InReview, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_leaves_task_phase_in_AwaitingReview()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var store = new InMemoryTaskStateStore();
        store.Set(StateWithNullPr());

        var activity = MakeActivity(github, store);
        await activity.RunAsync(new FakeCreatePrActivityContext(), Input(123));

        store.Current!.Phase.Should().Be(TaskPhase.AwaitingReview);
    }
}

file sealed class FakeCreatePrActivityContext : WorkflowActivityContext
{
    public override Dapr.Workflow.Abstractions.TaskIdentifier Identifier => "fake-task-name";
    public override string InstanceId => "fake-instance-id";
    public override string TaskExecutionKey => "fake-task-key";
}
