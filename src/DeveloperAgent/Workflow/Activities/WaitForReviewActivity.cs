using Dapr.Workflow;
using DeveloperAgent.GitHub;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Polls the GitHub PR once for its review status.
/// When the review state is <see cref="PullRequestReviewState.ChangesRequested"/>,
/// fetches review feedback from the beginning of the PR's review history.
/// The workflow loop calls this activity repeatedly (via <c>CreateTimer</c> between calls).
/// Returns <see cref="WaitForReviewResult"/> describing the current review state.
/// </summary>
public sealed class WaitForReviewActivity : WorkflowActivity<WaitForReviewActivityInput, WaitForReviewResult>
{
    private readonly ILogger<WaitForReviewActivity> _logger;
    private readonly IGitHubProjectService _github;

    public WaitForReviewActivity(
        ILogger<WaitForReviewActivity> logger,
        IGitHubProjectService github)
    {
        _logger = logger;
        _github = github;
    }

    public override async Task<WaitForReviewResult> RunAsync(
        WorkflowActivityContext context, WaitForReviewActivityInput input)
    {
        var ct = CancellationToken.None;
        var polledAt = DateTimeOffset.UtcNow;

        var status = await _github.GetPullRequestStatusAsync(input.PullRequestNumber, ct);

        _logger.LogInformation(
            "[{Activity}] item={ItemId} pr={PrNumber} review={Review} merged={Merged} checksGreen={ChecksGreen}",
            nameof(WaitForReviewActivity), input.ProjectItemId,
            input.PullRequestNumber, status.Review, status.Merged, status.ChecksGreen);

        string? feedbackMarkdown = null;
        if (status.Review == PullRequestReviewState.ChangesRequested)
        {
            // Fetch all feedback since the epoch so the workflow has the full picture.
            feedbackMarkdown = await _github.GetReviewFeedbackSinceAsync(
                input.PullRequestNumber,
                DateTimeOffset.MinValue,
                ct);
        }

        return new WaitForReviewResult(
            ReviewState: status.Review,
            Merged: status.Merged,
            ChecksGreen: status.ChecksGreen,
            FeedbackMarkdown: string.IsNullOrEmpty(feedbackMarkdown) ? null : feedbackMarkdown,
            PolledAtUtc: polledAt);
    }
}
