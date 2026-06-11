using Dapr.Workflow;
using DeveloperAgent.GitHub;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Squash-merges an approved pull request and deletes its head branch. Called by the review loop
/// once <see cref="WaitForReviewActivity"/> has observed the PR is approved, green, and mergeable.
/// </summary>
/// <remarks>
/// Idempotent by construction: <see cref="IGitHubProjectService.SquashMergePullRequestAsync"/> maps
/// an already-merged PR to <see cref="MergeOutcome.AlreadyMerged"/> and
/// <see cref="IGitHubProjectService.DeleteBranchAsync"/> tolerates a missing branch — so a workflow
/// retry or replay re-runs this activity safely. On a hard failure (<see cref="MergeOutcome.NotMergeable"/>)
/// the activity comments on the PR and leaves the branch alone; the workflow then parks the item in
/// In-review for a human.
/// </remarks>
public sealed class MergePullRequestActivity : WorkflowActivity<MergePullRequestActivityInput, MergePullRequestResult>
{
    private readonly ILogger<MergePullRequestActivity> _logger;
    private readonly IGitHubProjectService _github;

    public MergePullRequestActivity(ILogger<MergePullRequestActivity> logger, IGitHubProjectService github)
    {
        _logger = logger;
        _github = github;
    }

    public override async Task<MergePullRequestResult> RunAsync(
        WorkflowActivityContext context, MergePullRequestActivityInput input)
    {
        var ct = CancellationToken.None;

        var outcome = await _github.SquashMergePullRequestAsync(input.PullRequestNumber, ct);

        if (outcome == MergeOutcome.NotMergeable)
        {
            _logger.LogWarning(
                "[{Activity}] PR #{PrNumber} is not mergeable; leaving it for a human. item={ItemId}",
                nameof(MergePullRequestActivity), input.PullRequestNumber, input.ProjectItemId);

            await _github.AddItemCommentAsync(
                input.ContentNodeId,
                $"⚠️ Automated squash-merge of this PR failed: GitHub reports it is not mergeable " +
                "(merge conflict with the base branch, a failing required check, or branch protection). " +
                "The item has been left in **In-review** for a human to resolve.",
                ct);

            return new MergePullRequestResult(outcome);
        }

        _logger.LogInformation(
            "[{Activity}] PR #{PrNumber} merged ({Outcome}); deleting branch {Branch}. item={ItemId}",
            nameof(MergePullRequestActivity), input.PullRequestNumber, outcome, input.BranchName, input.ProjectItemId);

        await _github.DeleteBranchAsync(input.BranchName, ct);

        return new MergePullRequestResult(outcome);
    }
}
