using Dapr.Workflow;
using DeveloperAgent.Configuration;
using DeveloperAgent.GitHub;
using DeveloperAgent.Triage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Relevance-triage gate. Runs before the agent acquires/branches/plans an item and decides whether
/// the item is worth working. When triage is disabled this is a no-op that reports the item relevant.
/// When enabled and the item is judged out of scope, the activity comments on the item and moves it
/// from Ready to the write-only Backlog column, then reports it not-relevant so the workflow stops.
/// </summary>
/// <remarks>
/// The <see cref="TriageOptions.Enabled"/> gate lives here (not in the workflow body) so the workflow
/// stays deterministic and config-free: it always calls this activity and branches only on the
/// returned <see cref="TriageActivityResult.IsRelevant"/>.
/// </remarks>
public sealed class TriageActivity : WorkflowActivity<TriageActivityInput, TriageActivityResult>
{
    private readonly ILogger<TriageActivity> _logger;
    private readonly IGitHubProjectService _github;
    private readonly ITriageService _triage;
    private readonly TriageOptions _options;

    public TriageActivity(
        ILogger<TriageActivity> logger,
        IGitHubProjectService github,
        ITriageService triage,
        IOptions<TriageOptions> options)
    {
        _logger = logger;
        _github = github;
        _triage = triage;
        _options = options.Value;
    }

    public override async Task<TriageActivityResult> RunAsync(
        WorkflowActivityContext context, TriageActivityInput input)
    {
        var ct = CancellationToken.None;

        if (!_options.Enabled)
            return new TriageActivityResult(IsRelevant: true, Reason: "Triage disabled.");

        var verdict = await _triage.EvaluateAsync(input.Title, input.BodyMarkdown, ct);

        if (verdict.IsRelevant)
        {
            _logger.LogInformation(
                "[{Activity}] item={ItemId} relevant. reason={Reason}",
                nameof(TriageActivity), input.ProjectItemId, verdict.Reason);
            return new TriageActivityResult(IsRelevant: true, Reason: verdict.Reason);
        }

        // Out of scope: comment with the reason (human recovery path) and park in Backlog.
        var comment = TriageCommentFormatter.Format(verdict.Reason);
        await _github.AddItemCommentAsync(input.ContentNodeId, comment, ct);
        await _github.MoveItemAsync(
            input.ProjectItemId, ProjectState.Ready, ProjectState.Backlog, ct);

        _logger.LogInformation(
            "[{Activity}] item={ItemId} rejected and parked in Backlog. reason={Reason}",
            nameof(TriageActivity), input.ProjectItemId, verdict.Reason);

        return new TriageActivityResult(IsRelevant: false, Reason: verdict.Reason);
    }
}
