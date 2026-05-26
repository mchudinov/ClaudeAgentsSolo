using Dapr.Workflow;
using DeveloperAgent.Agent;
using DeveloperAgent.GitHub;
using DeveloperAgent.Lifecycle;
using DeveloperAgent.Workspace;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Runs subsequent agent rounds when the reviewer has requested changes.
/// Fetches review feedback, re-runs the agent, and returns the outcome.
/// Returns <see cref="ModifyCodeResult"/> with the agent outcome.
/// </summary>
public sealed class ModifyCodeActivity : WorkflowActivity<ModifyCodeActivityInput, ModifyCodeResult>
{
    private readonly ILogger<ModifyCodeActivity> _logger;
    private readonly IGitHubProjectService _github;
    private readonly IAgentRunner _agentRunner;
    private readonly ITaskStateStore _taskStateStore;

    public ModifyCodeActivity(
        ILogger<ModifyCodeActivity> logger,
        IGitHubProjectService github,
        IAgentRunner agentRunner,
        ITaskStateStore taskStateStore)
    {
        _logger = logger;
        _github = github;
        _agentRunner = agentRunner;
        _taskStateStore = taskStateStore;
    }

    public override async Task<ModifyCodeResult> RunAsync(
        WorkflowActivityContext context, ModifyCodeActivityInput input)
    {
        var ct = CancellationToken.None;

        // Move back to InProgress for the agent re-run
        await _github.MoveItemAsync(
            input.ProjectItemId, ProjectState.InReview, ProjectState.InProgress, ct);

        var current = _taskStateStore.Current;
        if (current is not null)
        {
            _taskStateStore.Set(current with
            {
                Phase = TaskPhase.AgentRunning,
                LastReviewPolledAtUtc = input.LastReviewPolledAtUtc,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
        }

        _logger.LogInformation(
            "[{Activity}] item={ItemId} prNumber={PrNumber} re-running agent after ChangesRequested",
            nameof(ModifyCodeActivity), input.ProjectItemId, input.PullRequestNumber);

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
            new AgentRunRequest(item, ws, PriorReviewFeedback: input.PriorReviewFeedback), ct);

        _logger.LogInformation(
            "[{Activity}] item={ItemId} outcome={Outcome}",
            nameof(ModifyCodeActivity), input.ProjectItemId, result.Outcome);

        if (result.Outcome != AgentRunOutcome.Completed)
        {
            var comment = FailureCommentFormatter.Format(result);
            await _github.AddItemCommentAsync(input.ContentNodeId, comment, ct);
            await _github.MoveItemAsync(
                input.ProjectItemId, ProjectState.InProgress, ProjectState.Ready, ct);

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
                "[{Activity}] Agent run failed during modify. item={ItemId} outcome={Outcome}",
                nameof(ModifyCodeActivity), input.ProjectItemId, result.Outcome);
        }

        return new ModifyCodeResult(result.Outcome, result.ToolCallsUsed, result.TerminationReason);
    }

    private static string? SanitizeLastError(AgentRunResult result) =>
        result.Outcome == AgentRunOutcome.SandboxViolation
            ? "Sandbox violation"
            : result.TerminationReason;
}
