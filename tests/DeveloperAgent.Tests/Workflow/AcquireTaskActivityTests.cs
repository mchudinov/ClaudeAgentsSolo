using Dapr.Workflow;
using DeveloperAgent.GitHub;
using DeveloperAgent.Lifecycle;
using DeveloperAgent.Workflow;
using DeveloperAgent.Workflow.Activities;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeveloperAgent.Tests.Workflow;

/// <summary>
/// Unit tests for <see cref="AcquireTaskActivity"/>.
/// Verifies that RunAsync calls MoveItemAsync(Ready→InProgress) and sets state to Acquired.
/// </summary>
public sealed class AcquireTaskActivityTests
{
    private static AcquireTaskActivityInput MakeInput(string itemId = "PVTI_abc") =>
        new(
            ProjectItemId: itemId,
            ContentNodeId: $"I_node_{itemId}",
            ContentNumber: 42,
            Title: "Test task",
            BodyMarkdown: "body");

    private static WorkflowActivityContext MakeContext() =>
        // WorkflowActivityContext has no public constructor — we use a fake subclass.
        new FakeWorkflowActivityContext();

    [Fact]
    public async Task RunAsync_moves_item_from_Ready_to_InProgress()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = Substitute.For<ITaskStateStore>();

        var activity = new AcquireTaskActivity(
            NullLogger<AcquireTaskActivity>.Instance,
            github,
            stateStore);

        var input = MakeInput("PVTI_test_1");
        var result = await activity.RunAsync(MakeContext(), input);

        await github.Received(1).MoveItemAsync(
            "PVTI_test_1",
            ProjectState.Ready,
            ProjectState.InProgress,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_sets_task_state_to_Acquired()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = Substitute.For<ITaskStateStore>();

        var activity = new AcquireTaskActivity(
            NullLogger<AcquireTaskActivity>.Instance,
            github,
            stateStore);

        var input = MakeInput("PVTI_test_2");
        await activity.RunAsync(MakeContext(), input);

        stateStore.Received(1).Set(
            Arg.Is<TaskState>(s =>
                s.ProjectItemId == "PVTI_test_2" &&
                s.Phase == TaskPhase.Acquired));
    }

    [Fact]
    public async Task RunAsync_returns_AcquireTaskResult_with_branch_name()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = Substitute.For<ITaskStateStore>();

        var activity = new AcquireTaskActivity(
            NullLogger<AcquireTaskActivity>.Instance,
            github,
            stateStore);

        var input = MakeInput("PVTI_test_3");
        var result = await activity.RunAsync(MakeContext(), input);

        result.Should().NotBeNull();
        result!.BranchName.Should().StartWith("agent/");
    }

    [Fact]
    public async Task RunAsync_sets_task_state_with_correct_issue_number_and_title()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = Substitute.For<ITaskStateStore>();

        var activity = new AcquireTaskActivity(
            NullLogger<AcquireTaskActivity>.Instance,
            github,
            stateStore);

        var input = new AcquireTaskActivityInput("PVTI_xyz", "I_node_xyz", 77, "Fix the bug", "body");
        await activity.RunAsync(MakeContext(), input);

        stateStore.Received(1).Set(
            Arg.Is<TaskState>(s =>
                s.IssueNumber == 77 &&
                s.Title == "Fix the bug" &&
                s.Phase == TaskPhase.Acquired));
    }
}

/// <summary>
/// Minimal concrete subclass of WorkflowActivityContext for testing.
/// WorkflowActivityContext has a protected/internal constructor, so we need a concrete fake.
/// </summary>
file sealed class FakeWorkflowActivityContext : WorkflowActivityContext
{
    public override Dapr.Workflow.Abstractions.TaskIdentifier Identifier => "fake-task-name";
    public override string InstanceId => "fake-instance-id";
    public override string TaskExecutionKey => "fake-task-key";
}
