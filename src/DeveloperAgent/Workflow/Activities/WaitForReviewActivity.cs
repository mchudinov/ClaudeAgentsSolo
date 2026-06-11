using Dapr.Workflow;
using DeveloperAgent.GitHub;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Polls the GitHub PR once for its review status.
/// When the review state is <see cref="PullRequestReviewState.ChangesRequested"/>,
/// fetches review feedback from the beginning of the PR's review history.
/// The workflow loop calls this activity repeatedly (via <c>CreateTimer</c> between calls,
/// or in response to external events).
/// Returns <see cref="WaitForReviewResult"/> describing the current review state.
/// </summary>
/// <remarks>
/// In addition to returning the status to its caller, the activity raises a workflow
/// external event back to the calling workflow instance so the workflow's
/// <c>WaitForExternalEventAsync</c> arms can win the next race:
/// <list type="bullet">
///   <item><c>ChangesRequested</c> when the PR review state is ChangesRequested.</item>
///   <item><c>ReadyToMerge</c> when the PR is approved, checks are green, and GitHub reports it mergeable.</item>
///   <item>No event is raised for Pending — the workflow's timer arm wins instead.</item>
/// </list>
/// The workflow instance ID is taken from <see cref="WorkflowActivityContext.InstanceId"/>.
/// </remarks>
public sealed class WaitForReviewActivity : WorkflowActivity<WaitForReviewActivityInput, WaitForReviewResult>
{
    /// <summary>Event name raised when the PR has been marked ChangesRequested.</summary>
    public const string ChangesRequestedEventName = "ChangesRequested";

    /// <summary>Event raised when the PR is approved, green, and mergeable — i.e. ready to be merged.</summary>
    public const string ReadyToMergeEventName = "ReadyToMerge";

    private readonly ILogger<WaitForReviewActivity> _logger;
    private readonly IGitHubProjectService _github;
    private readonly IDaprWorkflowClient _workflowClient;

    public WaitForReviewActivity(
        ILogger<WaitForReviewActivity> logger,
        IGitHubProjectService github,
        IDaprWorkflowClient workflowClient)
    {
        _logger = logger;
        _github = github;
        _workflowClient = workflowClient;
    }

    public override async Task<WaitForReviewResult> RunAsync(
        WorkflowActivityContext context, WaitForReviewActivityInput input)
    {
        var ct = CancellationToken.None;
        var polledAt = DateTimeOffset.UtcNow;

        var status = await _github.GetPullRequestStatusAsync(input.PullRequestNumber, ct);

        _logger.LogInformation(
            "[{Activity}] item={ItemId} pr={PrNumber} review={Review} merged={Merged} checksGreen={ChecksGreen} closed={Closed}",
            nameof(WaitForReviewActivity), input.ProjectItemId,
            input.PullRequestNumber, status.Review, status.Merged, status.ChecksGreen, status.Closed);

        string? feedbackMarkdown = null;
        if (status.Review == PullRequestReviewState.ChangesRequested)
        {
            // Fetch all feedback since the epoch so the workflow has the full picture.
            feedbackMarkdown = await _github.GetReviewFeedbackSinceAsync(
                input.PullRequestNumber,
                DateTimeOffset.MinValue,
                ct);
        }

        // Raise external event back to the parent workflow instance so the workflow's
        // WaitForExternalEventAsync arm wins immediately rather than waiting for the timer.
        // Pending → raise nothing (workflow's timer arm drives cadence).
        var payload = new ReviewEventPayload(input.PullRequestNumber);
        if (status.Review == PullRequestReviewState.Approved && status.ChecksGreen && status.Mergeable == true)
        {
            await _workflowClient.RaiseEventAsync(
                instanceId: context.InstanceId,
                eventName: ReadyToMergeEventName,
                eventPayload: payload,
                cancellation: ct);
        }
        else if (status.Review == PullRequestReviewState.ChangesRequested)
        {
            await _workflowClient.RaiseEventAsync(
                instanceId: context.InstanceId,
                eventName: ChangesRequestedEventName,
                eventPayload: payload,
                cancellation: ct);
        }

        return new WaitForReviewResult(
            ReviewState: status.Review,
            Merged: status.Merged,
            ChecksGreen: status.ChecksGreen,
            FeedbackMarkdown: string.IsNullOrEmpty(feedbackMarkdown) ? null : feedbackMarkdown,
            PolledAtUtc: polledAt,
            Mergeable: status.Mergeable,
            Closed: status.Closed);
    }
}
