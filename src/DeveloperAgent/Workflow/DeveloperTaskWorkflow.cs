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
/// Activity sequence:
/// 1. <see cref="AcquireTaskActivity"/> — Ready → InProgress, set initial state.
/// 2. <see cref="CreateBranchActivity"/> — prepare workspace and checkout branch.
/// 3. <see cref="PlanActivity"/> — first agent round; agent opens PR on success.
///    On failure: call <see cref="DoneActivity"/> (failure, no PR) → release InProgress → Ready → return "Failed".
///    If no PR: same failure path.
/// 4. <see cref="CreatePullRequestActivity"/> — InProgress → InReview.
/// 5. Review loop:
///    a. <see cref="WaitForReviewActivity"/> — poll PR status once and raise external event.
///       - <c>Merged</c>   if merged && approved
///       - <c>ChangesRequested</c> if review state is ChangesRequested
///       - (no event for Pending)
///    b. Race <see cref="WorkflowContext.WaitForExternalEventAsync{T}(string, System.Threading.CancellationToken)"/>
///       for <c>Merged</c> and <c>ChangesRequested</c> against a <see cref="WorkflowContext.CreateTimer(System.TimeSpan, System.Threading.CancellationToken)"/>
///       (timer cadence drives re-polling when nothing happens).
///    c. On <c>Merged</c> → <see cref="DoneActivity"/> (success) → return "Done".
///    d. On <c>ChangesRequested</c> → <see cref="ModifyCodeActivity"/> → back to step 5a.
///    e. On timer → back to step 5a (re-poll).
///
/// Recovery fast-path (<c>RecoveryAlreadyMerged</c>):
/// Scheduled by lifecycle on startup when an InReview item was merged out-of-band.
/// Jumps straight to <see cref="DoneActivity"/> (success) without running Acquire/Branch/Plan.
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

        // ── 0. RECOVERY FAST-PATH ───────────────────────────────────────────────
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
            return new TaskResult("Done");
        }

        // ── 1. ACQUIRE ──────────────────────────────────────────────────────────
        var acquireInput = new AcquireTaskActivityInput(
            input.ProjectItemId, input.ContentNodeId, input.ContentNumber,
            input.Title, input.BodyMarkdown);

        var acquireResult = await context.CallActivityAsync<AcquireTaskResult>(
            nameof(AcquireTaskActivity), acquireInput, retryOptions);

        // ── 2. WORKSPACE / BRANCH ───────────────────────────────────────────────
        var branchInput = new CreateBranchActivityInput(
            input.ProjectItemId, input.ContentNodeId, input.ContentNumber,
            input.Title, input.BodyMarkdown, acquireResult.BranchName);

        var branchResult = await context.CallActivityAsync<CreateBranchResult>(
            nameof(CreateBranchActivity), branchInput, retryOptions);

        // ── 3. PLAN (agent round 1) ─────────────────────────────────────────────
        var planInput = new PlanActivityInput(
            input.ProjectItemId, input.ContentNodeId, input.ContentNumber,
            input.Title, input.BodyMarkdown,
            acquireResult.BranchName,
            branchResult.WorkspacePath,
            branchResult.DefaultBranch);

        var planResult = await context.CallActivityAsync<PlanResult>(
            nameof(PlanActivity), planInput, retryOptions);

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
            return new TaskResult("Failed");
        }

        var prNumber = planResult.PullRequestNumber.Value;

        // ── 4. CREATE PR (move board item to InReview; PR was opened by agent during PlanActivity) ──
        var createPrInput = new CreatePullRequestActivityInput(
            ProjectItemId: input.ProjectItemId,
            ContentNodeId: input.ContentNodeId,
            ContentNumber: input.ContentNumber,
            Title: input.Title,
            BranchName: acquireResult.BranchName);

        await context.CallActivityAsync<CreatePullRequestResult>(
            nameof(CreatePullRequestActivity), createPrInput, retryOptions);

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

            // Direct decision when the poll itself observed a terminal state.
            if (lastReviewResult.Merged && lastReviewResult.ReviewState == PullRequestReviewState.Approved)
            {
                return await CompleteWithSuccessAsync(context, input, branchResult,
                    acquireResult.BranchName, prNumber, toolCallsUsed, retryOptions);
            }

            if (lastReviewResult.ReviewState == PullRequestReviewState.ChangesRequested)
            {
                var modifyResult = await CallModifyCodeAsync(
                    context, input, branchResult, acquireResult.BranchName, prNumber,
                    lastReviewResult.FeedbackMarkdown ?? string.Empty,
                    lastReviewResult.PolledAtUtc, retryOptions);

                toolCallsUsed += modifyResult.ToolCallsUsed;

                if (modifyResult.Outcome != AgentRunOutcome.Completed)
                {
                    return await CompleteWithFailureAsync(context, input, branchResult,
                        acquireResult.BranchName, prNumber, toolCallsUsed, retryOptions);
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
                    acquireResult.BranchName, prNumber, toolCallsUsed, retryOptions);
            }

            if (winner == changesEventTask)
            {
                // We don't have fresh feedback markdown yet — pass what the last poll captured.
                // ModifyCodeActivity itself is responsible for re-fetching feedback if needed.
                var modifyResult = await CallModifyCodeAsync(
                    context, input, branchResult, acquireResult.BranchName, prNumber,
                    lastReviewResult.FeedbackMarkdown ?? string.Empty,
                    lastReviewResult.PolledAtUtc, retryOptions);

                toolCallsUsed += modifyResult.ToolCallsUsed;

                if (modifyResult.Outcome != AgentRunOutcome.Completed)
                {
                    return await CompleteWithFailureAsync(context, input, branchResult,
                        acquireResult.BranchName, prNumber, toolCallsUsed, retryOptions);
                }

                continue;
            }

            // Timer won → loop and re-poll.
        }
    }

    private static async Task<TaskResult> CompleteWithSuccessAsync(
        WorkflowContext context, TaskInput input, CreateBranchResult branchResult,
        string branchName, int prNumber, long toolCallsUsed, WorkflowTaskOptions retryOptions)
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
        return new TaskResult("Done");
    }

    private static async Task<TaskResult> CompleteWithFailureAsync(
        WorkflowContext context, TaskInput input, CreateBranchResult branchResult,
        string branchName, int prNumber, long toolCallsUsed, WorkflowTaskOptions retryOptions)
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
}
