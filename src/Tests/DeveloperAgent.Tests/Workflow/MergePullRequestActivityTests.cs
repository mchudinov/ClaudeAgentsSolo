using Dapr.Workflow;
using DeveloperAgent.GitHub;
using DeveloperAgent.Workflow;
using DeveloperAgent.Workflow.Activities;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeveloperAgent.Tests.Workflow;

public sealed class MergePullRequestActivityTests
{
    private static MergePullRequestActivityInput Input(int pr = 7) =>
        new(ProjectItemId: "PVTI_abc", ContentNodeId: "PR_node", PullRequestNumber: pr, BranchName: "agent/feature-x");

    // The existing FakeWaitForReviewActivityContext is `file`-scoped in another file (not visible
    // here), so a local one is defined at the bottom of this file.
    private static WorkflowActivityContext Ctx() => new FakeMergeActivityContext();

    [Fact]
    public async Task Merges_then_deletes_branch_on_success()
    {
        var github = Substitute.For<IGitHubProjectService>();
        github.SquashMergePullRequestAsync(7, Arg.Any<CancellationToken>()).Returns(MergeOutcome.Merged);

        var activity = new MergePullRequestActivity(NullLogger<MergePullRequestActivity>.Instance, github);
        var result = await activity.RunAsync(Ctx(), Input());

        result.Succeeded.Should().BeTrue();
        await github.Received(1).SquashMergePullRequestAsync(7, Arg.Any<CancellationToken>());
        await github.Received(1).DeleteBranchAsync("agent/feature-x", Arg.Any<CancellationToken>());
        await github.DidNotReceiveWithAnyArgs().AddItemCommentAsync(default!, default!, default);
    }

    [Fact]
    public async Task AlreadyMerged_is_treated_as_success_and_still_deletes_branch()
    {
        var github = Substitute.For<IGitHubProjectService>();
        github.SquashMergePullRequestAsync(7, Arg.Any<CancellationToken>()).Returns(MergeOutcome.AlreadyMerged);

        var activity = new MergePullRequestActivity(NullLogger<MergePullRequestActivity>.Instance, github);
        var result = await activity.RunAsync(Ctx(), Input());

        result.Succeeded.Should().BeTrue();
        await github.Received(1).DeleteBranchAsync("agent/feature-x", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotMergeable_comments_and_does_not_delete_branch()
    {
        var github = Substitute.For<IGitHubProjectService>();
        github.SquashMergePullRequestAsync(7, Arg.Any<CancellationToken>()).Returns(MergeOutcome.NotMergeable);

        var activity = new MergePullRequestActivity(NullLogger<MergePullRequestActivity>.Instance, github);
        var result = await activity.RunAsync(Ctx(), Input());

        result.Succeeded.Should().BeFalse();
        result.Outcome.Should().Be(MergeOutcome.NotMergeable);
        await github.Received(1).AddItemCommentAsync("PR_node", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await github.DidNotReceiveWithAnyArgs().DeleteBranchAsync(default!, default);
    }
}

file sealed class FakeMergeActivityContext : WorkflowActivityContext
{
    public override Dapr.Workflow.Abstractions.TaskIdentifier Identifier => "fake-task-name";
    public override string InstanceId => "fake-instance-id";
    public override string TaskExecutionKey => "fake-task-key";
}
