using Agent.Workspace;
using Dapr.Workflow;
using DeveloperAgent.Configuration;
using DeveloperAgent.GitHub;
using DeveloperAgent.Resolution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Already-resolved gate. Runs after the branch/workspace is prepared but before the agent plans or
/// implements, and decides whether the work the item describes is already present in the real working
/// tree. When the gate is disabled this is a no-op that reports the item not-resolved (proceed). When
/// enabled and the item is judged already implemented, the activity reads the item's comments, posts an
/// "already implemented — confirm and mark Done" comment, moves the item from In Progress to the
/// write-only Backlog column, releases the workspace, and reports it resolved so the workflow stops.
/// </summary>
/// <remarks>
/// The <see cref="ResolutionCheckOptions.Enabled"/> gate lives here (not in the workflow body) so the
/// workflow stays deterministic and config-free: it always calls this activity and branches only on the
/// returned <see cref="ResolutionCheckActivityResult.IsAlreadyResolved"/>. Modeled on
/// <see cref="TriageActivity"/>; the one addition is releasing the workspace, because — unlike triage —
/// this gate runs after a working tree has been checked out.
/// </remarks>
public sealed class CheckAlreadyResolvedActivity
    : WorkflowActivity<ResolutionCheckActivityInput, ResolutionCheckActivityResult>
{
    private readonly ILogger<CheckAlreadyResolvedActivity> _logger;
    private readonly IGitHubProjectService _github;
    private readonly IResolutionChecker _checker;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ResolutionCheckOptions _options;

    public CheckAlreadyResolvedActivity(
        ILogger<CheckAlreadyResolvedActivity> logger,
        IGitHubProjectService github,
        IResolutionChecker checker,
        IWorkspaceManager workspaceManager,
        IOptions<ResolutionCheckOptions> options)
    {
        _logger = logger;
        _github = github;
        _checker = checker;
        _workspaceManager = workspaceManager;
        _options = options.Value;
    }

    public override async Task<ResolutionCheckActivityResult> RunAsync(
        WorkflowActivityContext context, ResolutionCheckActivityInput input)
    {
        var ct = CancellationToken.None;

        if (!_options.Enabled)
            return new ResolutionCheckActivityResult(false, "Resolution check disabled.");

        // "Read item text AND all comments, consider whether it was really developed against real code."
        // The activity gathers the comments; the checker is handed item text + comments + the working
        // tree path and is responsible for inspecting the real code and failing open on any uncertainty.
        // The fetch itself is fail-open: this optional gate must never block work, so a GitHub read
        // failure degrades to empty comments (and, almost always, a not-resolved verdict) rather than
        // throwing and leaving the item stuck in In Progress after Dapr exhausts its retries.
        string comments;
        try
        {
            comments = await _github.GetItemCommentsAsync(input.ContentNodeId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[{Activity}] Failed to read item comments; proceeding with none. item={ItemId}",
                nameof(CheckAlreadyResolvedActivity), input.ProjectItemId);
            comments = string.Empty;
        }

        var verdict = await _checker.EvaluateAsync(
            input.Title, input.BodyMarkdown, comments, input.WorkspacePath, ct);

        if (!verdict.IsAlreadyResolved)
        {
            _logger.LogInformation(
                "[{Activity}] item={ItemId} not already resolved; proceeding. reason={Reason}",
                nameof(CheckAlreadyResolvedActivity), input.ProjectItemId, verdict.Reason);
            return new ResolutionCheckActivityResult(false, verdict.Reason);
        }

        // Already implemented: record WHY (comment first so the reason is attached even if the move
        // fails), park the item In Progress → Backlog, then release the workspace. Mirrors the
        // self-contained TriageActivity rejection path, plus the workspace release this post-branch
        // gate is responsible for.
        var comment = ResolutionCommentFormatter.Format(verdict.Reason);
        await _github.AddItemCommentAsync(input.ContentNodeId, comment, ct);
        await _github.MoveItemAsync(
            input.ProjectItemId, ProjectState.InProgress, ProjectState.Backlog, ct);

        try
        {
            var ws = new TaskWorkspace(
                ProjectItemId: input.ProjectItemId,
                BranchName: input.BranchName,
                RepoRoot: input.WorkspacePath,
                DefaultBranch: input.DefaultBranch);

            await _workspaceManager.ReleaseAsync(ws, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[{Activity}] Failed to release workspace after already-resolved park. item={ItemId} path={Path}",
                nameof(CheckAlreadyResolvedActivity), input.ProjectItemId, input.WorkspacePath);
        }

        _logger.LogInformation(
            "[{Activity}] item={ItemId} already implemented; parked in Backlog. reason={Reason}",
            nameof(CheckAlreadyResolvedActivity), input.ProjectItemId, verdict.Reason);

        return new ResolutionCheckActivityResult(true, verdict.Reason);
    }
}
