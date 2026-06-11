using Dapr.Workflow;
using DeveloperAgent.GitHub;
using DeveloperAgent.Workflow;
using DeveloperAgent.Workflow.Activities;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeveloperAgent.Tests.Workflow;

/// <summary>
/// Unit tests for <see cref="WaitForReviewActivity"/>.
/// Verifies the activity returns the correct WaitForReviewResult for each PullRequestReviewState
/// and raises the right external workflow events.
/// </summary>
public sealed class WaitForReviewActivityTests
{
    private const string FakeInstanceId = "fake-instance-id";

    private static WaitForReviewActivityInput MakeInput(int prNumber = 99) =>
        new(ProjectItemId: "PVTI_abc", PullRequestNumber: prNumber);

    private static WorkflowActivityContext MakeContext() => new FakeWaitForReviewActivityContext();

    [Fact]
    public async Task RunAsync_returns_Pending_when_review_is_Pending()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var workflowClient = Substitute.For<IDaprWorkflowClient>();
        github.GetPullRequestStatusAsync(99, Arg.Any<CancellationToken>())
            .Returns(new PullRequestStatus(99, PullRequestReviewState.Pending, false, false, "abc", null));
        github.GetReviewFeedbackSinceAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);

        var activity = new WaitForReviewActivity(
            NullLogger<WaitForReviewActivity>.Instance,
            github,
            workflowClient);

        var result = await activity.RunAsync(MakeContext(), MakeInput(99));

        result.Should().NotBeNull();
        result!.ReviewState.Should().Be(PullRequestReviewState.Pending);
        result.Merged.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_returns_Approved_and_Merged_when_PR_is_merged_and_approved()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var workflowClient = Substitute.For<IDaprWorkflowClient>();
        github.GetPullRequestStatusAsync(99, Arg.Any<CancellationToken>())
            .Returns(new PullRequestStatus(99, PullRequestReviewState.Approved, true, true, "def", null));
        github.GetReviewFeedbackSinceAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);

        var activity = new WaitForReviewActivity(
            NullLogger<WaitForReviewActivity>.Instance,
            github,
            workflowClient);

        var result = await activity.RunAsync(MakeContext(), MakeInput(99));

        result.Should().NotBeNull();
        result!.ReviewState.Should().Be(PullRequestReviewState.Approved);
        result.Merged.Should().BeTrue();
        result.ChecksGreen.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_returns_ChangesRequested_and_fetches_feedback()
    {
        const string feedbackText = "Please fix the null check on line 42.";
        var github = Substitute.For<IGitHubProjectService>();
        var workflowClient = Substitute.For<IDaprWorkflowClient>();
        github.GetPullRequestStatusAsync(55, Arg.Any<CancellationToken>())
            .Returns(new PullRequestStatus(55, PullRequestReviewState.ChangesRequested, false, false, "ghi", null));
        github.GetReviewFeedbackSinceAsync(55, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(feedbackText);

        var activity = new WaitForReviewActivity(
            NullLogger<WaitForReviewActivity>.Instance,
            github,
            workflowClient);

        var result = await activity.RunAsync(MakeContext(), MakeInput(55));

        result.Should().NotBeNull();
        result!.ReviewState.Should().Be(PullRequestReviewState.ChangesRequested);
        result.FeedbackMarkdown.Should().Be(feedbackText);
        result.Merged.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_calls_GetPullRequestStatusAsync_with_correct_pr_number()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var workflowClient = Substitute.For<IDaprWorkflowClient>();
        github.GetPullRequestStatusAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PullRequestStatus(77, PullRequestReviewState.Pending, false, false, "jkl", null));
        github.GetReviewFeedbackSinceAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);

        var activity = new WaitForReviewActivity(
            NullLogger<WaitForReviewActivity>.Instance,
            github,
            workflowClient);

        await activity.RunAsync(MakeContext(), MakeInput(77));

        await github.Received(1).GetPullRequestStatusAsync(77, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_returns_PolledAtUtc_close_to_now()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var workflowClient = Substitute.For<IDaprWorkflowClient>();
        github.GetPullRequestStatusAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PullRequestStatus(99, PullRequestReviewState.Pending, false, false, "mno", null));
        github.GetReviewFeedbackSinceAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);

        var activity = new WaitForReviewActivity(
            NullLogger<WaitForReviewActivity>.Instance,
            github,
            workflowClient);

        var before = DateTimeOffset.UtcNow;
        var result = await activity.RunAsync(MakeContext(), MakeInput(99));
        var after = DateTimeOffset.UtcNow;

        result!.PolledAtUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public async Task RunAsync_surfaces_Closed_when_PR_is_closed_without_merging()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var workflowClient = Substitute.For<IDaprWorkflowClient>();
        github.GetPullRequestStatusAsync(99, Arg.Any<CancellationToken>())
            .Returns(new PullRequestStatus(99, PullRequestReviewState.Pending, ChecksGreen: true,
                Merged: false, "abc", Mergeable: null, Closed: true));
        github.GetReviewFeedbackSinceAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);

        var activity = new WaitForReviewActivity(
            NullLogger<WaitForReviewActivity>.Instance,
            github,
            workflowClient);

        var result = await activity.RunAsync(MakeContext(), MakeInput(99));

        result!.Closed.Should().BeTrue();
        // A closed-unmerged PR is terminal for the workflow's own branch — the activity raises no event.
        await workflowClient.DidNotReceiveWithAnyArgs().RaiseEventAsync(default!, default!, default!, default);
    }

    // ── External event raising (Step-15 P2-D part 3/3) ────────────────────────

    [Fact]
    public async Task RunAsync_raises_ChangesRequested_event_when_review_is_ChangesRequested()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var workflowClient = Substitute.For<IDaprWorkflowClient>();
        github.GetPullRequestStatusAsync(42, Arg.Any<CancellationToken>())
            .Returns(new PullRequestStatus(42, PullRequestReviewState.ChangesRequested, false, false, "sha", null));
        github.GetReviewFeedbackSinceAsync(42, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns("please fix");

        var activity = new WaitForReviewActivity(
            NullLogger<WaitForReviewActivity>.Instance,
            github,
            workflowClient);

        await activity.RunAsync(MakeContext(), MakeInput(42));

        await workflowClient.Received(1).RaiseEventAsync(
            FakeInstanceId,
            "ChangesRequested",
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_raises_ReadyToMerge_event_when_approved_green_and_mergeable()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var workflowClient = Substitute.For<IDaprWorkflowClient>();
        github.GetPullRequestStatusAsync(7, Arg.Any<CancellationToken>())
            .Returns(new PullRequestStatus(7, PullRequestReviewState.Approved, ChecksGreen: true, Merged: false, "sha7", Mergeable: true));
        github.GetReviewFeedbackSinceAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);

        var activity = new WaitForReviewActivity(
            NullLogger<WaitForReviewActivity>.Instance,
            github,
            workflowClient);

        await activity.RunAsync(MakeContext(), MakeInput(7));

        await workflowClient.Received(1).RaiseEventAsync(
            FakeInstanceId,
            "ReadyToMerge",
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_does_not_raise_ReadyToMerge_when_checks_not_green()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var workflowClient = Substitute.For<IDaprWorkflowClient>();
        github.GetPullRequestStatusAsync(8, Arg.Any<CancellationToken>())
            .Returns(new PullRequestStatus(8, PullRequestReviewState.Approved, ChecksGreen: false, Merged: false, "sha8", Mergeable: true));
        github.GetReviewFeedbackSinceAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);

        var activity = new WaitForReviewActivity(
            NullLogger<WaitForReviewActivity>.Instance,
            github,
            workflowClient);

        await activity.RunAsync(MakeContext(), MakeInput(8));

        await workflowClient.DidNotReceiveWithAnyArgs().RaiseEventAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task RunAsync_does_not_raise_ReadyToMerge_when_mergeability_unknown()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var workflowClient = Substitute.For<IDaprWorkflowClient>();
        github.GetPullRequestStatusAsync(9, Arg.Any<CancellationToken>())
            .Returns(new PullRequestStatus(9, PullRequestReviewState.Approved, ChecksGreen: true, Merged: false, "sha9", Mergeable: null));
        github.GetReviewFeedbackSinceAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);

        var activity = new WaitForReviewActivity(
            NullLogger<WaitForReviewActivity>.Instance,
            github,
            workflowClient);

        await activity.RunAsync(MakeContext(), MakeInput(9));

        await workflowClient.DidNotReceiveWithAnyArgs().RaiseEventAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task RunAsync_does_not_raise_any_event_when_review_is_Pending()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var workflowClient = Substitute.For<IDaprWorkflowClient>();
        github.GetPullRequestStatusAsync(99, Arg.Any<CancellationToken>())
            .Returns(new PullRequestStatus(99, PullRequestReviewState.Pending, false, false, "abc", null));
        github.GetReviewFeedbackSinceAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);

        var activity = new WaitForReviewActivity(
            NullLogger<WaitForReviewActivity>.Instance,
            github,
            workflowClient);

        await activity.RunAsync(MakeContext(), MakeInput(99));

        await workflowClient.DidNotReceiveWithAnyArgs().RaiseEventAsync(
            default!, default!, default!, default);
    }
}

file sealed class FakeWaitForReviewActivityContext : WorkflowActivityContext
{
    public override Dapr.Workflow.Abstractions.TaskIdentifier Identifier => "fake-task-name";
    public override string InstanceId => "fake-instance-id";
    public override string TaskExecutionKey => "fake-task-key";
}
