using Dapr.Workflow;
using DeveloperAgent.GitHub;
using DeveloperAgent.Lifecycle;
using DeveloperAgent.Observability;
using DeveloperAgent.Workflow;
using DeveloperAgent.Workflow.Activities;
using Agent.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeveloperAgent.Tests.Workflow;

/// <summary>
/// Unit tests for <see cref="DoneActivity"/>.
/// Covers all three lifecycle-completion paths:
///   1. Success                       → MoveItemAsync(InReview → Done)
///   2. Failure with no PR opened    → MoveItemAsync(InProgress → Ready) (release back)
///   3. Failure with a PR opened     → no MoveItemAsync (workflow keeps the item in InReview)
/// </summary>
public sealed class DoneActivityTests
{
    private static WorkflowActivityContext MakeContext() => new FakeDoneActivityContext();

    private static DoneActivity BuildActivity(
        IGitHubProjectService github,
        IWorkspaceManager? workspaceManager = null,
        ITaskStateStore? stateStore = null,
        AgentMetrics? metrics = null) =>
        new(
            NullLogger<DoneActivity>.Instance,
            github,
            workspaceManager ?? Substitute.For<IWorkspaceManager>(),
            stateStore ?? Substitute.For<ITaskStateStore>(),
            metrics ?? new AgentMetrics());

    [Fact]
    public async Task Success_path_moves_item_InReview_to_Done()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var activity = BuildActivity(github);

        var input = new DoneActivityInput(
            ProjectItemId: "PVTI_abc",
            ContentNodeId: "I_node",
            WorkspacePath: string.Empty,
            BranchName: "agent/branch",
            DefaultBranch: "main",
            PullRequestNumber: 12,
            Success: true,
            ToolCallsUsed: 7);

        await activity.RunAsync(MakeContext(), input);

        await github.Received(1).MoveItemAsync(
            "PVTI_abc",
            ProjectState.InReview,
            ProjectState.Done,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failure_path_with_no_PR_releases_item_back_to_Ready()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var activity = BuildActivity(github);

        var input = new DoneActivityInput(
            ProjectItemId: "PVTI_abc",
            ContentNodeId: "I_node",
            WorkspacePath: string.Empty,
            BranchName: "agent/branch",
            DefaultBranch: "main",
            PullRequestNumber: null,
            Success: false,
            ToolCallsUsed: 0);

        await activity.RunAsync(MakeContext(), input);

        // Item should be released InProgress → Ready (workflow's failure path before a PR opened).
        await github.Received(1).MoveItemAsync(
            "PVTI_abc",
            ProjectState.InProgress,
            ProjectState.Ready,
            Arg.Any<CancellationToken>());

        // It must NOT also move it to Done.
        await github.DidNotReceive().MoveItemAsync(
            Arg.Any<string>(),
            Arg.Any<ProjectState>(),
            ProjectState.Done,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failure_path_with_PR_does_not_move_item()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var activity = BuildActivity(github);

        // PR exists but workflow terminated unsuccessfully (e.g., ModifyCode failed).
        // The workflow leaves the item in InReview — the reviewer can take it forward
        // manually. DoneActivity must not move it.
        var input = new DoneActivityInput(
            ProjectItemId: "PVTI_abc",
            ContentNodeId: "I_node",
            WorkspacePath: string.Empty,
            BranchName: "agent/branch",
            DefaultBranch: "main",
            PullRequestNumber: 99,
            Success: false,
            ToolCallsUsed: 5);

        await activity.RunAsync(MakeContext(), input);

        await github.DidNotReceiveWithAnyArgs().MoveItemAsync(default!, default, default, default);
    }
}

internal sealed class FakeDoneActivityContext : WorkflowActivityContext
{
    public override Dapr.Workflow.Abstractions.TaskIdentifier Identifier => "done";
    public override string InstanceId => "github-project-item-PVTI_abc";
    public override string TaskExecutionKey => "exec-key";
}
