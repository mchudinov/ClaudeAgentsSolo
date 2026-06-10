using Dapr.Workflow;
using DeveloperAgent.Agent;
using DeveloperAgent.GitHub;
using DeveloperAgent.Workflow.Activities;

namespace DeveloperAgent.Workflow;

/// <summary>
/// Dapr Workflow that drives the full lifecycle of a single GitHub project item.
/// </summary>
/// <remarks>
/// Workflow instance ID convention: <c>github-project-item-{itemId}</c>.
/// Scheduled by <see cref="DeveloperAgent.Lifecycle.AgentLifecycleService"/> via
/// <c>DaprWorkflowClient.ScheduleNewWorkflowAsync(nameof(DeveloperTaskWorkflow),
/// instanceId: $"github-project-item-{input.ProjectItemId}", input: input)</c>.
///
/// Activity sequence (Step-18 adds Load/Save/Delete around the existing activities):
/// 0. <see cref="LoadAgentSessionActivity"/> — load persisted progress for crash-resume.
/// 1. <see cref="AcquireTaskActivity"/> → <see cref="SaveAgentSessionActivity"/>.
/// 2. <see cref="CreateBranchActivity"/> → <see cref="SaveAgentSessionActivity"/>.
/// 3. <see cref="PlanActivity"/> → <see cref="SaveAgentSessionActivity"/>.
///    On failure: <see cref="DoneActivity"/> → save → <see cref="DeleteAgentSessionActivity"/>.
/// 4. <see cref="CreatePullRequestActivity"/> → <see cref="SaveAgentSessionActivity"/>.
/// 5. Review loop:
///    a. <see cref="WaitForReviewActivity"/> → <see cref="SaveAgentSessionActivity"/>.
///    b. Race external events against a cadence timer.
///    c. On <c>Merged</c> → <see cref="DoneActivity"/> (success) → save → delete → "Done".
///    d. On <c>ChangesRequested</c> → <see cref="ModifyCodeActivity"/> → save → re-poll.
///    e. On timer → loop back to (a).
///
/// Recovery fast-path (<c>RecoveryAlreadyMerged</c>): Load → <see cref="DoneActivity"/> → save → delete.
///
/// Step-18 persistence determinism:
/// The workflow body is replay-safe — it never calls <c>DateTimeOffset.UtcNow</c> or
/// <c>DaprClient</c>. Each <see cref="SaveAgentSessionActivity"/> is given only
/// <c>(ProjectItemId, CurrentStep, Summary)</c>; the activity stamps
/// <see cref="AgentSession.LastActivityTimestampUtc"/> internally.
/// </remarks>
public sealed class DeveloperTaskWorkflow : Workflow<TaskInput, TaskResult>
{
    /// <summary>How long to wait between poll-for-review calls when status is Pending.</summary>
    private static readonly TimeSpan ReviewPollInterval = TimeSpan.FromMinutes(1);

    /// <summary>Maximum delay between activity retry attempts.</summary>
    private static readonly TimeSpan MaxRetryInterval = TimeSpan.FromMinutes(1);

    public override async Task<TaskResult> RunAsync(WorkflowContext context, TaskInput input)
    {
        var retryOptions = new WorkflowTaskOptions
        {
            RetryPolicy = new WorkflowRetryPolicy(
                maxNumberOfAttempts: input.MaxRetryAttempts,
                firstRetryInterval: TimeSpan.FromSeconds(input.FirstRetryIntervalSeconds),
                backoffCoefficient: 2.0,
                maxRetryInterval: MaxRetryInterval,
                retryTimeout: Timeout.InfiniteTimeSpan)
        };

        // ── 0. LOAD AGENT SESSION ───────────────────────────────────────────────
        // Resume any prior progress for this item, or initialise a fresh session if
        // none exists. Step-18 carries Summary forward without consuming it; Step-20
        // will plumb Summary into the agent kickoff.
        // TODO Step-20: plumb session.Summary into kickoff
        var session = await context.CallActivityAsync<AgentSession>(
            nameof(LoadAgentSessionActivity),
            new LoadAgentSessionActivityInput(input.ProjectItemId),
            retryOptions);

        // ── 0a. RECOVERY FAST-PATH ──────────────────────────────────────────────
        // The dispatcher detected that this item's PR was merged out-of-band before
        // restart. Skip the agent phases and let DoneActivity perform the InReview → Done
        // state transition + cleanup. No workspace was prepared in this branch.
        if (input.RecoveryAlreadyMerged)
        {
            var recoveryDoneInput = new DoneActivityInput(
                ProjectItemId: input.ProjectItemId,
                ContentNodeId: input.ContentNodeId,
                WorkspacePath: string.Empty,
                BranchName: input.RecoveryBranchName,
                DefaultBranch: string.Empty,
                PullRequestNumber: input.RecoveryPullRequestNumber,
                Success: true,
                ToolCallsUsed: 0);

            await context.CallActivityAsync<object?>(
                nameof(DoneActivity), recoveryDoneInput, retryOptions);
            await SaveSessionAsync(context, input.ProjectItemId, nameof(DoneActivity), session.Summary, retryOptions);
            await DeleteSessionAsync(context, input.ProjectItemId, retryOptions);
            return new TaskResult("Done");
        }

        // ── 0b. TRIAGE (relevance gate) ─────────────────────────────────────────
        // Before any expensive work, decide whether this item is in-scope for the project AND within
        // the agent's skill. The Enabled gate lives inside the activity (disabled → always relevant),
        // so the workflow body stays deterministic and config-free. On rejection the activity has
        // already commented and moved the item to the write-only Backlog column, so we just clean up
        // the freshly-loaded session and stop. A null result (e.g. an unconfigured test context) is
        // treated as relevant — the gate fails open and never blocks legitimate work.
        var triageInput = new TriageActivityInput(
            input.ProjectItemId, input.ContentNodeId, input.ContentNumber,
            input.Title, input.BodyMarkdown);

        var triageResult = await context.CallActivityAsync<TriageActivityResult>(
            nameof(TriageActivity), triageInput, retryOptions);

        if (triageResult is { IsRelevant: false })
        {
            await DeleteSessionAsync(context, input.ProjectItemId, retryOptions);
            return new TaskResult("Rejected");
        }

        // ── 1. ACQUIRE ──────────────────────────────────────────────────────────
        var acquireInput = new AcquireTaskActivityInput(
            input.ProjectItemId, input.ContentNodeId, input.ContentNumber,
            input.Title, input.BodyMarkdown);

        var acquireResult = await context.CallActivityAsync<AcquireTaskResult>(
            nameof(AcquireTaskActivity), acquireInput, retryOptions);
        await SaveSessionAsync(context, input.ProjectItemId, nameof(AcquireTaskActivity), session.Summary, retryOptions);

        // ── 2. WORKSPACE / BRANCH ───────────────────────────────────────────────
        var branchInput = new CreateBranchActivityInput(
            input.ProjectItemId, input.ContentNodeId, input.ContentNumber,
            input.Title, input.BodyMarkdown, acquireResult.BranchName);

        var branchResult = await context.CallActivityAsync<CreateBranchResult>(
            nameof(CreateBranchActivity), branchInput, retryOptions);
        await SaveSessionAsync(context, input.ProjectItemId, nameof(CreateBranchActivity), session.Summary, retryOptions);

        // ── 3. PLAN (agent round 1) ─────────────────────────────────────────────
        var planInput = new PlanActivityInput(
            input.ProjectItemId, input.ContentNodeId, input.ContentNumber,
            input.Title, input.BodyMarkdown,
            acquireResult.BranchName,
            branchResult.WorkspacePath,
            branchResult.DefaultBranch);

        var planResult = await context.CallActivityAsync<PlanResult>(
            nameof(PlanActivity), planInput, retryOptions);
        await SaveSessionAsync(context, input.ProjectItemId, nameof(PlanActivity), session.Summary, retryOptions);

        // Failure or no PR opened → release the item and terminate.
        if (planResult.Outcome != AgentRunOutcome.Completed || planResult.PullRequestNumber is null)
        {
            var doneFailInput = new DoneActivityInput(
                ProjectItemId: input.ProjectItemId,
                ContentNodeId: input.ContentNodeId,
                WorkspacePath: branchResult.WorkspacePath,
                BranchName: acquireResult.BranchName,
                DefaultBranch: branchResult.DefaultBranch,
                PullRequestNumber: null,
                Success: false,
                ToolCallsUsed: planResult.ToolCallsUsed);

            await context.CallActivityAsync<object?>(nameof(DoneActivity), doneFailInput, retryOptions);
            await SaveSessionAsync(context, input.ProjectItemId, nameof(DoneActivity), session.Summary, retryOptions);
            await DeleteSessionAsync(context, input.ProjectItemId, retryOptions);
            return new TaskResult("Failed");
        }

        var prNumber = planResult.PullRequestNumber.Value;

        // ── 4. CREATE PR (move board item to InReview; PR was opened by agent during PlanActivity) ──
        var createPrInput = new CreatePullRequestActivityInput(
            ProjectItemId: input.ProjectItemId,
            ContentNodeId: input.ContentNodeId,
            ContentNumber: input.ContentNumber,
            Title: input.Title,
            BranchName: acquireResult.BranchName,
            PullRequestNumber: prNumber);

        await context.CallActivityAsync<CreatePullRequestResult>(
            nameof(CreatePullRequestActivity), createPrInput, retryOptions);
        await SaveSessionAsync(context, input.ProjectItemId, nameof(CreatePullRequestActivity), session.Summary, retryOptions);

        // ── 5. REVIEW LOOP ──────────────────────────────────────────────────────
        long toolCallsUsed = planResult.ToolCallsUsed;
        WaitForReviewResult? lastReviewResult = null;

        while (true)
        {
            // Poll once. The activity also raises an external event (Merged / ChangesRequested)
            // when applicable. The race below short-circuits on that event so the next iteration
            // of the loop branches immediately without an additional poll.
            var waitInput = new WaitForReviewActivityInput(input.ProjectItemId, prNumber);
            lastReviewResult = await context.CallActivityAsync<WaitForReviewResult>(
                nameof(WaitForReviewActivity), waitInput, retryOptions);
            await SaveSessionAsync(context, input.ProjectItemId, nameof(WaitForReviewActivity), session.Summary, retryOptions);

            // Direct decision when the poll itself observed a terminal state.
            if (lastReviewResult.Merged && lastReviewResult.ReviewState == PullRequestReviewState.Approved)
            {
                return await CompleteWithSuccessAsync(context, input, branchResult,
                    acquireResult.BranchName, prNumber, toolCallsUsed, session.Summary, retryOptions);
            }

            if (lastReviewResult.ReviewState == PullRequestReviewState.ChangesRequested)
            {
                var modifyResult = await CallModifyCodeAsync(
                    context, input, branchResult, acquireResult.BranchName, prNumber,
                    lastReviewResult.FeedbackMarkdown ?? string.Empty,
                    lastReviewResult.PolledAtUtc, retryOptions);
                await SaveSessionAsync(context, input.ProjectItemId, nameof(ModifyCodeActivity), session.Summary, retryOptions);

                toolCallsUsed += modifyResult.ToolCallsUsed;

                if (modifyResult.Outcome != AgentRunOutcome.Completed)
                {
                    return await CompleteWithFailureAsync(context, input, branchResult,
                        acquireResult.BranchName, prNumber, toolCallsUsed, session.Summary, retryOptions);
                }

                continue;
            }

            // ── Pending: race external events against a cadence timer ───────────
            // Whoever wins drives the next iteration of the loop:
            //   • Merged event           → complete success (branch immediately, no extra poll).
            //   • ChangesRequested event → invoke ModifyCodeActivity (branch immediately, no extra poll).
            //   • Timer elapsed          → loop back to re-poll (cadence driver).
            //
            // We share a CancellationTokenSource so the losing arms get cancelled. Dapr buffers
            // any event that arrives at a cancelled WaitForExternalEventAsync, so we won't drop signals.
            using var cts = new CancellationTokenSource();
            var changesEventTask = context.WaitForExternalEventAsync<ReviewEventPayload>(
                WaitForReviewActivity.ChangesRequestedEventName, cts.Token);
            var mergedEventTask = context.WaitForExternalEventAsync<ReviewEventPayload>(
                WaitForReviewActivity.MergedEventName, cts.Token);
            var timerTask = context.CreateTimer(ReviewPollInterval, cts.Token);

            var winner = await Task.WhenAny(changesEventTask, mergedEventTask, timerTask);
            cts.Cancel();

            if (winner == mergedEventTask)
            {
                return await CompleteWithSuccessAsync(context, input, branchResult,
                    acquireResult.BranchName, prNumber, toolCallsUsed, session.Summary, retryOptions);
            }

            if (winner == changesEventTask)
            {
                // We don't have fresh feedback markdown yet — pass what the last poll captured.
                // ModifyCodeActivity itself is responsible for re-fetching feedback if needed.
                var modifyResult = await CallModifyCodeAsync(
                    context, input, branchResult, acquireResult.BranchName, prNumber,
                    lastReviewResult.FeedbackMarkdown ?? string.Empty,
                    lastReviewResult.PolledAtUtc, retryOptions);
                await SaveSessionAsync(context, input.ProjectItemId, nameof(ModifyCodeActivity), session.Summary, retryOptions);

                toolCallsUsed += modifyResult.ToolCallsUsed;

                if (modifyResult.Outcome != AgentRunOutcome.Completed)
                {
                    return await CompleteWithFailureAsync(context, input, branchResult,
                        acquireResult.BranchName, prNumber, toolCallsUsed, session.Summary, retryOptions);
                }

                continue;
            }

            // Timer won → loop and re-poll.
        }
    }

    private static async Task<TaskResult> CompleteWithSuccessAsync(
        WorkflowContext context, TaskInput input, CreateBranchResult branchResult,
        string branchName, int prNumber, long toolCallsUsed, string? summary, WorkflowTaskOptions retryOptions)
    {
        var doneSuccessInput = new DoneActivityInput(
            ProjectItemId: input.ProjectItemId,
            ContentNodeId: input.ContentNodeId,
            WorkspacePath: branchResult.WorkspacePath,
            BranchName: branchName,
            DefaultBranch: branchResult.DefaultBranch,
            PullRequestNumber: prNumber,
            Success: true,
            ToolCallsUsed: toolCallsUsed);

        await context.CallActivityAsync<object?>(nameof(DoneActivity), doneSuccessInput, retryOptions);
        await SaveSessionAsync(context, input.ProjectItemId, nameof(DoneActivity), summary, retryOptions);

        // ── Step-25 (P2-J): compact task memory after the Done transition ──────────
        // Persist a task-completion summary under task-memory:{projectItemId} so a future
        // run on the same item reads it back via DaprAgentMemoryContextProvider (Step-20).
        // Only plain deterministic data is passed; CompactMemoryActivity stamps the
        // completion timestamp and writes state itself (workflow body stays replay-safe).
        // The recovery fast-path deliberately skips this — see RunAsync's RecoveryAlreadyMerged
        // branch: a PR merged out-of-band did no agent work this run, so there is nothing to
        // summarize.
        var compactInput = new CompactMemoryActivityInput(
            ProjectItemId: input.ProjectItemId,
            Title: input.Title,
            ContentNumber: input.ContentNumber,
            BranchName: branchName,
            PullRequestNumber: prNumber,
            ToolCallsUsed: toolCallsUsed,
            Decisions: null,
            ChangedFiles: Array.Empty<string>(),
            TestResults: null,
            UnresolvedRisks: null);

        await context.CallActivityAsync<object?>(
            nameof(CompactMemoryActivity), compactInput, retryOptions);

        await DeleteSessionAsync(context, input.ProjectItemId, retryOptions);
        return new TaskResult("Done");
    }

    private static async Task<TaskResult> CompleteWithFailureAsync(
        WorkflowContext context, TaskInput input, CreateBranchResult branchResult,
        string branchName, int prNumber, long toolCallsUsed, string? summary, WorkflowTaskOptions retryOptions)
    {
        var doneFailInput = new DoneActivityInput(
            ProjectItemId: input.ProjectItemId,
            ContentNodeId: input.ContentNodeId,
            WorkspacePath: branchResult.WorkspacePath,
            BranchName: branchName,
            DefaultBranch: branchResult.DefaultBranch,
            PullRequestNumber: prNumber,
            Success: false,
            ToolCallsUsed: toolCallsUsed);

        await context.CallActivityAsync<object?>(nameof(DoneActivity), doneFailInput, retryOptions);
        await SaveSessionAsync(context, input.ProjectItemId, nameof(DoneActivity), summary, retryOptions);
        await DeleteSessionAsync(context, input.ProjectItemId, retryOptions);
        return new TaskResult("Failed");
    }

    private static async Task<ModifyCodeResult> CallModifyCodeAsync(
        WorkflowContext context, TaskInput input, CreateBranchResult branchResult,
        string branchName, int prNumber, string feedback, DateTimeOffset polledAt,
        WorkflowTaskOptions retryOptions)
    {
        var modifyInput = new ModifyCodeActivityInput(
            ProjectItemId: input.ProjectItemId,
            ContentNodeId: input.ContentNodeId,
            ContentNumber: input.ContentNumber,
            Title: input.Title,
            BodyMarkdown: input.BodyMarkdown,
            BranchName: branchName,
            WorkspacePath: branchResult.WorkspacePath,
            DefaultBranch: branchResult.DefaultBranch,
            PullRequestNumber: prNumber,
            PriorReviewFeedback: feedback,
            LastReviewPolledAtUtc: polledAt);

        return await context.CallActivityAsync<ModifyCodeResult>(
            nameof(ModifyCodeActivity), modifyInput, retryOptions);
    }

    /// <summary>
    /// Schedules a <see cref="SaveAgentSessionActivity"/>. Workflow code is deterministic —
    /// the activity stamps <see cref="AgentSession.LastActivityTimestampUtc"/> itself.
    /// </summary>
    private static Task<object?> SaveSessionAsync(
        WorkflowContext context, string projectItemId, string currentStep, string? summary,
        WorkflowTaskOptions retryOptions) =>
        context.CallActivityAsync<object?>(
            nameof(SaveAgentSessionActivity),
            new SaveAgentSessionActivityInput(projectItemId, currentStep, summary),
            retryOptions);

    /// <summary>Schedules the workflow-tail <see cref="DeleteAgentSessionActivity"/>.</summary>
    private static Task<object?> DeleteSessionAsync(
        WorkflowContext context, string projectItemId, WorkflowTaskOptions retryOptions) =>
        context.CallActivityAsync<object?>(
            nameof(DeleteAgentSessionActivity),
            new DeleteAgentSessionActivityInput(projectItemId),
            retryOptions);
}
