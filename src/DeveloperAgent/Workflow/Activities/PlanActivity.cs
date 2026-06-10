using Dapr.Workflow;
using DeveloperAgent.Agent;
using DeveloperAgent.GitHub;
using DeveloperAgent.Lifecycle;
using Agent.Workspace;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Runs the first agent round (plan + implement + open PR).
/// Updates state to <see cref="TaskPhase.AgentRunning"/>.
/// Returns <see cref="PlanResult"/> with the agent outcome and optional PR number.
/// </summary>
public sealed class PlanActivity : WorkflowActivity<PlanActivityInput, PlanResult>
{
    private readonly ILogger<PlanActivity> _logger;
    private readonly IGitHubProjectService _github;
    private readonly IAgentRunner _agentRunner;
    private readonly ITaskStateStore _taskStateStore;

    public PlanActivity(
        ILogger<PlanActivity> logger,
        IGitHubProjectService github,
        IAgentRunner agentRunner,
        ITaskStateStore taskStateStore)
    {
        _logger = logger;
        _github = github;
        _agentRunner = agentRunner;
        _taskStateStore = taskStateStore;
    }

    public override async Task<PlanResult> RunAsync(
        WorkflowActivityContext context, PlanActivityInput input)
    {
        var ct = CancellationToken.None;

        var current = _taskStateStore.Current;
        if (current is not null)
        {
            _taskStateStore.Set(current with
            {
                Phase = TaskPhase.AgentRunning,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
        }

        _logger.LogInformation(
            "[{Activity}] item={ItemId} round=1 phase={Phase}",
            nameof(PlanActivity), input.ProjectItemId, TaskPhase.AgentRunning);

        var item = new ProjectItem(
            ProjectItemId: input.ProjectItemId,
            ContentNodeId: input.ContentNodeId,
            ContentNumber: input.ContentNumber,
            Title: input.Title,
            BodyMarkdown: input.BodyMarkdown,
            State: ProjectState.InProgress);

        var ws = new TaskWorkspace(
            ProjectItemId: input.ProjectItemId,
            BranchName: input.BranchName,
            RepoRoot: input.WorkspacePath,
            DefaultBranch: input.DefaultBranch);

        var result = await _agentRunner.RunAsync(
            new AgentRunRequest(item, ws, PriorReviewFeedback: null), ct);

        _logger.LogInformation(
            "[{Activity}] item={ItemId} outcome={Outcome} pr={PR}",
            nameof(PlanActivity), input.ProjectItemId, result.Outcome, result.PullRequest?.Number);

        if (result.Outcome != AgentRunOutcome.Completed)
        {
            // Post the human-facing failure explanation, but do NOT move the board item here.
            // Step-53 makes DoneActivity the single owner of the failure transition (InProgress →
            // Backlog). Moving the item to Ready from this activity caused a flicker race where a
            // poll tick could re-grab the item mid-teardown — and ultimately an infinite redispatch
            // loop. The workflow always follows a non-Completed PlanResult with DoneActivity, which
            // performs the parking move.
            // FailureCommentFormatter is disposition-neutral (shared with ModifyCodeActivity, which
            // leaves the item in InReview, and the legacy TaskExecutor). DoneActivity will park this
            // item in the write-only Backlog column, where the poller never re-grabs it, so we append
            // the parking + recovery notice here — at the call site that knows the disposition —
            // mirroring Step-54's TriageCommentFormatter.
            var comment = FailureCommentFormatter.Format(result) + "\n\n" + BacklogParkingNotice;
            await _github.AddItemCommentAsync(input.ContentNodeId, comment, ct);

            if (current is not null)
            {
                _taskStateStore.Set(current with
                {
                    Phase = TaskPhase.Failed,
                    LastError = SanitizeLastError(result),
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });
            }

            _logger.LogError(
                "[{Activity}] Agent run failed on round 1. item={ItemId} outcome={Outcome}",
                nameof(PlanActivity), input.ProjectItemId, result.Outcome);
        }

        return new PlanResult(result.Outcome, result.PullRequest?.Number, result.ToolCallsUsed, result.TerminationReason);
    }

    /// <summary>
    /// Recovery notice appended to the round-1 failure comment. Backlog is write-only, so this is the
    /// only signal a maintainer gets that the item was parked and needs a manual nudge to re-run.
    /// </summary>
    private const string BacklogParkingNotice =
        "**Auto-parked in Backlog after a failed implementation attempt.** " +
        "The agent will not retry this item automatically — move it back to **Ready** to re-queue it.";

    private static string? SanitizeLastError(AgentRunResult result) =>
        result.Outcome == AgentRunOutcome.SandboxViolation
            ? "Sandbox violation"
            : result.TerminationReason;
}
