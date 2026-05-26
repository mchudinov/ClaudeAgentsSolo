using DeveloperAgent.Agent;
using DeveloperAgent.Configuration;
using DeveloperAgent.GitHub;
using DeveloperAgent.Observability;
using DeveloperAgent.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Lifecycle;

/// <summary>
/// Drives a single GitHub Project item from <see cref="ProjectState.Ready"/>
/// all the way to <see cref="ProjectState.Done"/> (or to a terminal failure) by
/// coordinating GitHub, workspace, git, and agent services.
/// </summary>
public sealed class TaskExecutor(
    ILogger<TaskExecutor> logger,
    IOptions<AgentOptions> agentOptions,
    IGitHubProjectService github,
    IWorkspaceManager workspaceManager,
    IGitClient gitClient,
    IAgentRunner agentRunner,
    ITaskStateStore taskStateStore,
    TimeProvider timeProvider,
    AgentMetrics metrics) : ITaskExecutor
{
    private readonly AgentOptions _options = agentOptions.Value;

    /// <summary>
    /// Runs the full per-item state machine for <paramref name="item"/>.
    /// Returns when the item reaches Done, a terminal failure, or the token is cancelled.
    /// </summary>
    public async Task<TaskExecutionResult> RunAsync(ProjectItem item, CancellationToken ct)
    {
        var startedAt = timeProvider.GetUtcNow();

        // ── 1. ACQUIRE ────────────────────────────────────────────────────────
        await github.MoveItemAsync(item.ProjectItemId, ProjectState.Ready, ProjectState.InProgress, ct);

        var branchName = BranchName.ForTask(threadId: null, item.Title);

        var state = new TaskState(
            ProjectItemId: item.ProjectItemId,
            IssueNumber: item.ContentNumber,
            Title: item.Title,
            Phase: TaskPhase.Acquired,
            BranchName: null,
            PullRequestNumber: null,
            LastError: null,
            StartedAtUtc: startedAt,
            UpdatedAtUtc: timeProvider.GetUtcNow(),
            PullRequestOpenedAtUtc: null,
            LastReviewPolledAtUtc: null);

        taskStateStore.Set(state);

        logger.LogInformation(
            "Task acquired. {ItemId} {IssueNumber} {Title} {Phase}",
            item.ProjectItemId, item.ContentNumber, item.Title, TaskPhase.Acquired);

        // ── 2. WORKSPACE ──────────────────────────────────────────────────────
        var ws = await workspaceManager.PrepareAsync(item.ProjectItemId, branchName, ct);
        await gitClient.CheckoutNewBranchAsync(ws, ct);

        state = state with
        {
            Phase = TaskPhase.WorkspaceReady,
            BranchName = branchName,
            UpdatedAtUtc = timeProvider.GetUtcNow()
        };
        taskStateStore.Set(state);

        logger.LogInformation(
            "Workspace ready. {ItemId} {Phase} {BranchName}",
            item.ProjectItemId, TaskPhase.WorkspaceReady, branchName);

        // ── 3. AGENT ROUND 1 ──────────────────────────────────────────────────
        int round = 1;
        AgentRunResult result;

        state = state with { Phase = TaskPhase.AgentRunning, UpdatedAtUtc = timeProvider.GetUtcNow() };
        taskStateStore.Set(state);

        logger.LogInformation(
            "Agent running. {ItemId} {Phase} Round={Round}",
            item.ProjectItemId, TaskPhase.AgentRunning, round);

        result = await agentRunner.RunAsync(new AgentRunRequest(item, ws, PriorReviewFeedback: null), ct);

        if (result.Outcome != AgentRunOutcome.Completed)
        {
            var comment = FailureCommentFormatter.Format(result);
            await github.AddItemCommentAsync(item.ContentNodeId, comment, ct);
            await github.MoveItemAsync(item.ProjectItemId, ProjectState.InProgress, ProjectState.Ready, ct);

            state = state with
            {
                Phase = TaskPhase.Failed,
                LastError = SanitizeLastError(result),
                UpdatedAtUtc = timeProvider.GetUtcNow()
            };
            taskStateStore.Set(state);

            logger.LogError(
                "Agent run failed on round {Round}. {ItemId} {Outcome} {TerminationReason}",
                round, item.ProjectItemId, result.Outcome, result.TerminationReason);

            metrics.RecordTaskTerminated(success: false, toolCalls: result.ToolCallsUsed);
            return new TaskExecutionResult(TaskOutcome.Failed);
        }

        if (result.PullRequest is null)
        {
            const string noPrComment = "Agent finished without opening a PR — releasing.";
            await github.AddItemCommentAsync(item.ContentNodeId, noPrComment, ct);
            await github.MoveItemAsync(item.ProjectItemId, ProjectState.InProgress, ProjectState.Ready, ct);

            state = state with
            {
                Phase = TaskPhase.Failed,
                LastError = "Agent finished without opening a PR.",
                UpdatedAtUtc = timeProvider.GetUtcNow()
            };
            taskStateStore.Set(state);

            logger.LogError(
                "Agent completed but opened no PR. {ItemId}",
                item.ProjectItemId);

            metrics.RecordTaskTerminated(success: false, toolCalls: result.ToolCallsUsed);
            return new TaskExecutionResult(TaskOutcome.Failed);
        }

        var pr = result.PullRequest;
        var prOpenedAt = timeProvider.GetUtcNow();
        metrics.RecordTimeToPullRequest(prOpenedAt - startedAt);

        // ── 4. PR OPENED ──────────────────────────────────────────────────────
        state = state with
        {
            Phase = TaskPhase.PullRequestOpen,
            PullRequestNumber = pr.Number,
            PullRequestOpenedAtUtc = prOpenedAt,
            LastReviewPolledAtUtc = prOpenedAt,
            UpdatedAtUtc = timeProvider.GetUtcNow()
        };
        taskStateStore.Set(state);

        logger.LogInformation(
            "PR opened. {ItemId} {PullRequestNumber} {Phase}",
            item.ProjectItemId, pr.Number, TaskPhase.PullRequestOpen);

        // ── 5. MOVE TO IN REVIEW ──────────────────────────────────────────────
        await github.MoveItemAsync(item.ProjectItemId, ProjectState.InProgress, ProjectState.InReview, ct);

        state = state with { Phase = TaskPhase.AwaitingReview, UpdatedAtUtc = timeProvider.GetUtcNow() };
        taskStateStore.Set(state);

        logger.LogInformation(
            "Awaiting review. {ItemId} {Phase}",
            item.ProjectItemId, TaskPhase.AwaitingReview);

        // ── 6. REVIEW WAIT LOOP ───────────────────────────────────────────────
        using var reviewTimer = new PeriodicTimer(
            TimeSpan.FromSeconds(_options.ReviewPollIntervalSeconds),
            timeProvider);

        while (await reviewTimer.WaitForNextTickAsync(ct))
        {
            var status = await github.GetPullRequestStatusAsync(pr.Number, ct);

            if (status.Merged && status.ChecksGreen && status.Review == PullRequestReviewState.Approved)
            {
                await github.MoveItemAsync(item.ProjectItemId, ProjectState.InReview, ProjectState.Done, ct);

                state = state with { Phase = TaskPhase.Done, UpdatedAtUtc = timeProvider.GetUtcNow() };
                taskStateStore.Set(state);

                logger.LogInformation(
                    "Task done. {ItemId} {Phase} ElapsedFromStart={ElapsedFromStart}",
                    item.ProjectItemId, TaskPhase.Done, timeProvider.GetUtcNow() - startedAt);

                // ── 7. CLEANUP (Done only) ─────────────────────────────────
                await workspaceManager.ReleaseAsync(ws, ct);

                metrics.RecordTaskTerminated(success: true, toolCalls: result.ToolCallsUsed);
                return new TaskExecutionResult(TaskOutcome.Done);
            }

            if (status.Review == PullRequestReviewState.ChangesRequested)
            {
                round++;

                // Fetch feedback BEFORE advancing cursor so a transient error leaves
                // the cursor pointing at the previous tick (safe replay).
                var feedback = await github.GetReviewFeedbackSinceAsync(
                    pr.Number,
                    state.LastReviewPolledAtUtc!.Value,
                    ct);

                // Advance cursor only after a successful fetch.
                state = state with
                {
                    LastReviewPolledAtUtc = timeProvider.GetUtcNow(),
                    UpdatedAtUtc = timeProvider.GetUtcNow()
                };
                taskStateStore.Set(state);

                await github.MoveItemAsync(item.ProjectItemId, ProjectState.InReview, ProjectState.InProgress, ct);

                state = state with { Phase = TaskPhase.AgentRunning, UpdatedAtUtc = timeProvider.GetUtcNow() };
                taskStateStore.Set(state);

                logger.LogInformation(
                    "Changes requested; re-running agent. {ItemId} Round={Round}",
                    item.ProjectItemId, round);

                result = await agentRunner.RunAsync(
                    new AgentRunRequest(item, ws, PriorReviewFeedback: feedback), ct);

                if (result.Outcome != AgentRunOutcome.Completed)
                {
                    var comment = FailureCommentFormatter.Format(result);
                    await github.AddItemCommentAsync(item.ContentNodeId, comment, ct);
                    await github.MoveItemAsync(item.ProjectItemId, ProjectState.InProgress, ProjectState.Ready, ct);

                    state = state with
                    {
                        Phase = TaskPhase.Failed,
                        LastError = SanitizeLastError(result),
                        UpdatedAtUtc = timeProvider.GetUtcNow()
                    };
                    taskStateStore.Set(state);

                    logger.LogError(
                        "Agent run failed on round {Round}. {ItemId} {Outcome} {TerminationReason}",
                        round, item.ProjectItemId, result.Outcome, result.TerminationReason);

                    metrics.RecordTaskTerminated(success: false, toolCalls: result.ToolCallsUsed);
                    return new TaskExecutionResult(TaskOutcome.Failed);
                }

                // Agent pushed more commits; move back to InReview and continue loop.
                await github.MoveItemAsync(item.ProjectItemId, ProjectState.InProgress, ProjectState.InReview, ct);

                state = state with { Phase = TaskPhase.AwaitingReview, UpdatedAtUtc = timeProvider.GetUtcNow() };
                taskStateStore.Set(state);

                logger.LogInformation(
                    "Re-submitted to review. {ItemId} Round={Round}",
                    item.ProjectItemId, round);

                continue;
            }

            // Pending or Approved-but-not-yet-merged — keep waiting.
            logger.LogInformation(
                "PR review status {Review}; waiting. {ItemId}",
                status.Review, item.ProjectItemId);
        }

        // WaitForNextTickAsync returned false — cancellation (stoppingToken).
        return new TaskExecutionResult(TaskOutcome.Cancelled);
    }

    /// <inheritdoc/>
    public async Task<TaskExecutionResult> ResumeReviewAsync(
        ProjectItem item, int pullRequestNumber, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();

        var state = new TaskState(
            ProjectItemId: item.ProjectItemId,
            IssueNumber: item.ContentNumber,
            Title: item.Title,
            Phase: TaskPhase.AwaitingReview,
            BranchName: null,
            PullRequestNumber: pullRequestNumber,
            LastError: null,
            StartedAtUtc: now,
            UpdatedAtUtc: now,
            PullRequestOpenedAtUtc: now,
            LastReviewPolledAtUtc: now);

        taskStateStore.Set(state);

        logger.LogInformation(
            "Resuming review wait after restart. {ItemId} PullRequestNumber={PullRequestNumber}",
            item.ProjectItemId, pullRequestNumber);

        int round = 1;
        AgentRunResult? lastResult = null;

        using var reviewTimer = new PeriodicTimer(
            TimeSpan.FromSeconds(_options.ReviewPollIntervalSeconds),
            timeProvider);

        while (await reviewTimer.WaitForNextTickAsync(ct))
        {
            var status = await github.GetPullRequestStatusAsync(pullRequestNumber, ct);

            if (status.Merged && status.ChecksGreen && status.Review == PullRequestReviewState.Approved)
            {
                await github.MoveItemAsync(item.ProjectItemId, ProjectState.InReview, ProjectState.Done, ct);

                state = state with { Phase = TaskPhase.Done, UpdatedAtUtc = timeProvider.GetUtcNow() };
                taskStateStore.Set(state);

                logger.LogInformation(
                    "Resumed task done. {ItemId} {Phase}",
                    item.ProjectItemId, TaskPhase.Done);

                metrics.RecordTaskTerminated(success: true, toolCalls: lastResult?.ToolCallsUsed ?? 0);
                return new TaskExecutionResult(TaskOutcome.Done);
            }

            if (status.Review == PullRequestReviewState.ChangesRequested)
            {
                round++;

                var feedback = await github.GetReviewFeedbackSinceAsync(
                    pullRequestNumber,
                    state.LastReviewPolledAtUtc!.Value,
                    ct);

                state = state with
                {
                    LastReviewPolledAtUtc = timeProvider.GetUtcNow(),
                    UpdatedAtUtc = timeProvider.GetUtcNow()
                };
                taskStateStore.Set(state);

                await github.MoveItemAsync(item.ProjectItemId, ProjectState.InReview, ProjectState.InProgress, ct);

                state = state with { Phase = TaskPhase.AgentRunning, UpdatedAtUtc = timeProvider.GetUtcNow() };
                taskStateStore.Set(state);

                logger.LogInformation(
                    "Changes requested on resumed review; re-running agent. {ItemId} Round={Round}",
                    item.ProjectItemId, round);

                var ws = await workspaceManager.PrepareAsync(item.ProjectItemId, BranchName.ForTask(null, item.Title), ct);
                await gitClient.CheckoutNewBranchAsync(ws, ct);

                lastResult = await agentRunner.RunAsync(
                    new AgentRunRequest(item, ws, PriorReviewFeedback: feedback), ct);

                if (lastResult.Outcome != AgentRunOutcome.Completed)
                {
                    var comment = FailureCommentFormatter.Format(lastResult);
                    await github.AddItemCommentAsync(item.ContentNodeId, comment, ct);
                    await github.MoveItemAsync(item.ProjectItemId, ProjectState.InProgress, ProjectState.Ready, ct);

                    state = state with
                    {
                        Phase = TaskPhase.Failed,
                        LastError = SanitizeLastError(lastResult),
                        UpdatedAtUtc = timeProvider.GetUtcNow()
                    };
                    taskStateStore.Set(state);

                    logger.LogError(
                        "Agent run failed during resumed review on round {Round}. {ItemId}",
                        round, item.ProjectItemId);

                    metrics.RecordTaskTerminated(success: false, toolCalls: lastResult.ToolCallsUsed);
                    return new TaskExecutionResult(TaskOutcome.Failed);
                }

                await github.MoveItemAsync(item.ProjectItemId, ProjectState.InProgress, ProjectState.InReview, ct);

                state = state with { Phase = TaskPhase.AwaitingReview, UpdatedAtUtc = timeProvider.GetUtcNow() };
                taskStateStore.Set(state);

                logger.LogInformation(
                    "Re-submitted to review after resumed session. {ItemId} Round={Round}",
                    item.ProjectItemId, round);

                continue;
            }

            logger.LogInformation(
                "PR review status {Review}; waiting. {ItemId}",
                status.Review, item.ProjectItemId);
        }

        return new TaskExecutionResult(TaskOutcome.Cancelled);
    }

    /// <summary>
    /// Returns a safe <see cref="TaskState.LastError"/> value.
    /// For <see cref="AgentRunOutcome.SandboxViolation"/> the termination reason is
    /// the offending command line, which must not be surfaced through the public /info
    /// endpoint.  All other outcomes pass the reason through unchanged.
    /// </summary>
    private static string? SanitizeLastError(AgentRunResult result) =>
        result.Outcome == AgentRunOutcome.SandboxViolation
            ? "Sandbox violation"
            : result.TerminationReason;
}
