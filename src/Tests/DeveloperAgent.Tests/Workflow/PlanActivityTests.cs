using Dapr.Workflow;
using DeveloperAgent.Agent;
using DeveloperAgent.GitHub;
using DeveloperAgent.Lifecycle;
using DeveloperAgent.Workflow;
using DeveloperAgent.Workflow.Activities;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeveloperAgent.Tests.Workflow;

/// <summary>
/// Unit tests for <see cref="PlanActivity"/> — agent round 1.
/// Step-53 focus: on a non-Completed outcome the activity still posts the failure comment, but it
/// no longer performs its own InProgress → Ready board move. DoneActivity is the single owner of
/// the failure transition (now InProgress → Backlog), which removes the Ready-flicker race where a
/// poll tick could re-grab the item mid-teardown.
/// </summary>
public sealed class PlanActivityTests
{
    private static PlanActivityInput MakeInput(string itemId = "PVTI_abc") =>
        new(
            ProjectItemId: itemId,
            ContentNodeId: $"I_node_{itemId}",
            ContentNumber: 42,
            Title: "Add feature X",
            BodyMarkdown: "body",
            BranchName: "agent/branch",
            WorkspacePath: "/ws/x",
            DefaultBranch: "main");

    private static PlanActivity Build(IGitHubProjectService github, IAgentRunner agentRunner) =>
        new(
            NullLogger<PlanActivity>.Instance,
            github,
            agentRunner,
            Substitute.For<ITaskStateStore>());

    private static IAgentRunner AgentReturning(AgentRunResult result)
    {
        var runner = Substitute.For<IAgentRunner>();
        runner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>()).Returns(result);
        return runner;
    }

    private static AgentRunResult Failure(AgentRunOutcome outcome = AgentRunOutcome.HardCapReached) =>
        new(outcome, PullRequest: null, TurnsUsed: 1, ToolCallsUsed: 3, TerminationReason: "ran out of turns");

    [Fact]
    public async Task Failure_does_not_move_the_item()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var activity = Build(github, AgentReturning(Failure()));

        await activity.RunAsync(new FakePlanActivityContext(), MakeInput());

        // The transition is owned solely by DoneActivity (InProgress → Backlog). PlanActivity must
        // not move the item at all — moving it to Ready here is the flicker race Step-53 removes.
        await github.DidNotReceiveWithAnyArgs().MoveItemAsync(default!, default, default, default);
    }

    [Fact]
    public async Task Failure_still_posts_the_failure_comment()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var input = MakeInput();
        var activity = Build(github, AgentReturning(Failure()));

        await activity.RunAsync(new FakePlanActivityContext(), input);

        // Removing the move must not remove the human-facing explanation — the comment stays.
        await github.Received(1).AddItemCommentAsync(
            input.ContentNodeId, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failure_comment_states_the_item_was_parked_in_Backlog_with_a_recovery_path()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var input = MakeInput();
        var activity = Build(github, AgentReturning(Failure()));

        await activity.RunAsync(new FakePlanActivityContext(), input);

        // Backlog is write-only — the poller never re-grabs a parked item. So the comment is the
        // human recovery path (mirroring Step-54's triage rejection): it must say the item was parked
        // in Backlog and how to re-queue it (move back to Ready).
        await github.Received(1).AddItemCommentAsync(
            input.ContentNodeId,
            Arg.Is<string>(c => c.Contains("Backlog") && c.Contains("Ready")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Success_neither_comments_nor_moves_the_item()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var success = new AgentRunResult(
            AgentRunOutcome.Completed, PullRequest: null, TurnsUsed: 3, ToolCallsUsed: 5, TerminationReason: null);
        var activity = Build(github, AgentReturning(success));

        await activity.RunAsync(new FakePlanActivityContext(), MakeInput());

        await github.DidNotReceiveWithAnyArgs().MoveItemAsync(default!, default, default, default);
        await github.DidNotReceiveWithAnyArgs().AddItemCommentAsync(default!, default!, default);
    }
}

/// <summary>Minimal concrete <see cref="WorkflowActivityContext"/> for activity unit tests.</summary>
file sealed class FakePlanActivityContext : WorkflowActivityContext
{
    public override Dapr.Workflow.Abstractions.TaskIdentifier Identifier => "plan";
    public override string InstanceId => "github-project-item-PVTI_abc";
    public override string TaskExecutionKey => "exec-key";
}
