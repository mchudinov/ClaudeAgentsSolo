using Dapr.Workflow;
using DeveloperAgent.GitHub;
using DeveloperAgent.Workflow;
using DeveloperAgent.Workflow.Activities;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeveloperAgent.Tests.Workflow;

/// <summary>
/// Unit tests for <see cref="WaitForReviewActivity"/>.
/// Verifies the activity returns the correct WaitForReviewResult for each PullRequestReviewState.
/// </summary>
public sealed class WaitForReviewActivityTests
{
    private static WaitForReviewActivityInput MakeInput(int prNumber = 99) =>
        new(ProjectItemId: "PVTI_abc", PullRequestNumber: prNumber);

    private static WorkflowActivityContext MakeContext() => new FakeWaitForReviewActivityContext();

    [Fact]
    public async Task RunAsync_returns_Pending_when_review_is_Pending()
    {
        var github = Substitute.For<IGitHubProjectService>();
        github.GetPullRequestStatusAsync(99, Arg.Any<CancellationToken>())
            .Returns(new PullRequestStatus(99, PullRequestReviewState.Pending, false, false, "abc"));
        github.GetReviewFeedbackSinceAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);

        var activity = new WaitForReviewActivity(
            NullLogger<WaitForReviewActivity>.Instance,
            github);

        var result = await activity.RunAsync(MakeContext(), MakeInput(99));

        result.Should().NotBeNull();
        result!.ReviewState.Should().Be(PullRequestReviewState.Pending);
        result.Merged.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_returns_Approved_and_Merged_when_PR_is_merged_and_approved()
    {
        var github = Substitute.For<IGitHubProjectService>();
        github.GetPullRequestStatusAsync(99, Arg.Any<CancellationToken>())
            .Returns(new PullRequestStatus(99, PullRequestReviewState.Approved, true, true, "def"));
        github.GetReviewFeedbackSinceAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);

        var activity = new WaitForReviewActivity(
            NullLogger<WaitForReviewActivity>.Instance,
            github);

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
        github.GetPullRequestStatusAsync(55, Arg.Any<CancellationToken>())
            .Returns(new PullRequestStatus(55, PullRequestReviewState.ChangesRequested, false, false, "ghi"));
        github.GetReviewFeedbackSinceAsync(55, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(feedbackText);

        var activity = new WaitForReviewActivity(
            NullLogger<WaitForReviewActivity>.Instance,
            github);

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
        github.GetPullRequestStatusAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PullRequestStatus(77, PullRequestReviewState.Pending, false, false, "jkl"));
        github.GetReviewFeedbackSinceAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);

        var activity = new WaitForReviewActivity(
            NullLogger<WaitForReviewActivity>.Instance,
            github);

        await activity.RunAsync(MakeContext(), MakeInput(77));

        await github.Received(1).GetPullRequestStatusAsync(77, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_returns_PolledAtUtc_close_to_now()
    {
        var github = Substitute.For<IGitHubProjectService>();
        github.GetPullRequestStatusAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PullRequestStatus(99, PullRequestReviewState.Pending, false, false, "mno"));
        github.GetReviewFeedbackSinceAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);

        var activity = new WaitForReviewActivity(
            NullLogger<WaitForReviewActivity>.Instance,
            github);

        var before = DateTimeOffset.UtcNow;
        var result = await activity.RunAsync(MakeContext(), MakeInput(99));
        var after = DateTimeOffset.UtcNow;

        result!.PolledAtUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }
}

file sealed class FakeWaitForReviewActivityContext : WorkflowActivityContext
{
    public override Dapr.Workflow.Abstractions.TaskIdentifier Identifier => "fake-task-name";
    public override string InstanceId => "fake-instance-id";
    public override string TaskExecutionKey => "fake-task-key";
}
