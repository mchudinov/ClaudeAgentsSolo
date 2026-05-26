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
///    On failure: call <see cref="DoneActivity"/> (failure) and return "Failed".
///    If no PR: return "Failed".
/// 4. <see cref="CreatePullRequestActivity"/> — InProgress → InReview.
/// 5. Loop:
///    a. <see cref="WaitForReviewActivity"/> — poll PR status once.
///    b. If Merged + Approved → <see cref="DoneActivity"/> (success) → return "Done".
///    c. If ChangesRequested → <see cref="ModifyCodeActivity"/> → back to step 5.
///    d. If Pending → wait via <see cref="WorkflowContext.CreateTimer"/> → back to step 5.
/// </remarks>
public sealed class DeveloperTaskWorkflow : Workflow<TaskInput, TaskResult>
{
    /// <summary>How long to wait between poll-for-review calls when status is Pending.</summary>
    private static readonly TimeSpan ReviewPollInterval = TimeSpan.FromMinutes(1);

    public override async Task<TaskResult> RunAsync(WorkflowContext context, TaskInput input)
    {
        // ── 1. ACQUIRE ──────────────────────────────────────────────────────────
        var acquireInput = new AcquireTaskActivityInput(
            input.ProjectItemId, input.ContentNodeId, input.ContentNumber,
            input.Title, input.BodyMarkdown);

        var acquireResult = await context.CallActivityAsync<AcquireTaskResult>(
            nameof(AcquireTaskActivity), acquireInput);

        // ── 2. WORKSPACE / BRANCH ───────────────────────────────────────────────
        var branchInput = new CreateBranchActivityInput(
            input.ProjectItemId, input.ContentNodeId, input.ContentNumber,
            input.Title, input.BodyMarkdown, acquireResult.BranchName);

        var branchResult = await context.CallActivityAsync<CreateBranchResult>(
            nameof(CreateBranchActivity), branchInput);

        // ── 3. PLAN (agent round 1) ─────────────────────────────────────────────
        var planInput = new PlanActivityInput(
            input.ProjectItemId, input.ContentNodeId, input.ContentNumber,
            input.Title, input.BodyMarkdown,
            acquireResult.BranchName,
            branchResult.WorkspacePath,
            branchResult.DefaultBranch);

        var planResult = await context.CallActivityAsync<PlanResult>(
            nameof(PlanActivity), planInput);

        // Failure or no PR opened → clean up and terminate
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

            await context.CallActivityAsync<object?>(nameof(DoneActivity), doneFailInput);
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
            nameof(CreatePullRequestActivity), createPrInput);

        // ── 5. REVIEW LOOP ──────────────────────────────────────────────────────
        long toolCallsUsed = planResult.ToolCallsUsed;

        while (true)
        {
            var waitInput = new WaitForReviewActivityInput(input.ProjectItemId, prNumber);
            var reviewResult = await context.CallActivityAsync<WaitForReviewResult>(
                nameof(WaitForReviewActivity), waitInput);

            if (reviewResult.Merged && reviewResult.ReviewState == PullRequestReviewState.Approved)
            {
                // ── 6. DONE (success) ───────────────────────────────────────────
                var doneSuccessInput = new DoneActivityInput(
                    ProjectItemId: input.ProjectItemId,
                    ContentNodeId: input.ContentNodeId,
                    WorkspacePath: branchResult.WorkspacePath,
                    BranchName: acquireResult.BranchName,
                    DefaultBranch: branchResult.DefaultBranch,
                    PullRequestNumber: prNumber,
                    Success: true,
                    ToolCallsUsed: toolCallsUsed);

                await context.CallActivityAsync<object?>(nameof(DoneActivity), doneSuccessInput);
                return new TaskResult("Done");
            }

            if (reviewResult.ReviewState == PullRequestReviewState.ChangesRequested)
            {
                // ── MODIFY (subsequent agent round) ─────────────────────────────
                var modifyInput = new ModifyCodeActivityInput(
                    ProjectItemId: input.ProjectItemId,
                    ContentNodeId: input.ContentNodeId,
                    ContentNumber: input.ContentNumber,
                    Title: input.Title,
                    BodyMarkdown: input.BodyMarkdown,
                    BranchName: acquireResult.BranchName,
                    WorkspacePath: branchResult.WorkspacePath,
                    DefaultBranch: branchResult.DefaultBranch,
                    PullRequestNumber: prNumber,
                    PriorReviewFeedback: reviewResult.FeedbackMarkdown ?? string.Empty,
                    LastReviewPolledAtUtc: reviewResult.PolledAtUtc);

                var modifyResult = await context.CallActivityAsync<ModifyCodeResult>(
                    nameof(ModifyCodeActivity), modifyInput);

                toolCallsUsed += modifyResult.ToolCallsUsed;

                if (modifyResult.Outcome != AgentRunOutcome.Completed)
                {
                    // Agent failed during modify — terminate
                    var doneFailInput = new DoneActivityInput(
                        ProjectItemId: input.ProjectItemId,
                        ContentNodeId: input.ContentNodeId,
                        WorkspacePath: branchResult.WorkspacePath,
                        BranchName: acquireResult.BranchName,
                        DefaultBranch: branchResult.DefaultBranch,
                        PullRequestNumber: prNumber,
                        Success: false,
                        ToolCallsUsed: toolCallsUsed);

                    await context.CallActivityAsync<object?>(nameof(DoneActivity), doneFailInput);
                    return new TaskResult("Failed");
                }

                // Agent pushed more commits — loop back to wait for new review
                continue;
            }

            // Pending — wait before polling again
            await context.CreateTimer(ReviewPollInterval);
        }
    }
}
