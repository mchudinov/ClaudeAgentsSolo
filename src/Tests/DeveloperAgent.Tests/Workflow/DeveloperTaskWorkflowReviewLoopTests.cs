using Dapr.Workflow;
using DeveloperAgent.Agent;
using DeveloperAgent.GitHub;
using DeveloperAgent.Workflow;
using DeveloperAgent.Workflow.Activities;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Tests.Workflow;

/// <summary>
/// Seam-level tests for the review-loop branching in <see cref="DeveloperTaskWorkflow"/>.
/// Uses a fake <see cref="WorkflowContext"/> that returns canned activity results, completed
/// external-event tasks, or completed timer tasks — to assert which branch the workflow
/// picks (event vs timer) and which activities it calls next.
/// </summary>
public sealed class DeveloperTaskWorkflowReviewLoopTests
{
    // ── Setup helpers ────────────────────────────────────────────────────────

    private static TaskInput Input(int prMaxRetry = 3, int firstRetrySec = 2) =>
        new("PVTI_abc", "I_node", 42, "Add feature X", "Body")
        {
            MaxRetryAttempts = prMaxRetry,
            FirstRetryIntervalSeconds = firstRetrySec
        };

    /// <summary>
    /// Pre-populates a fake context to skip cleanly through phases 1-4 of the workflow
    /// so each test can focus on the review-loop branch under test.
    /// </summary>
    private static void SetupHappyPathUntilReviewLoop(FakeWorkflowContext ctx, int prNumber = 7)
    {
        // Step-18: AgentSession persistence runs Load at start and Save between activities.
        // Tests don't assert on Load/Save/Delete here — see DeveloperTaskWorkflowSavesAgentSessionTests
        // — but the canned results are needed so RunAsync doesn't trip on default(T) casts.
        ctx.SetActivityResult(nameof(LoadAgentSessionActivity),
            new AgentSession(Environment.MachineName, "PVTI_abc", string.Empty,
                new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero), Summary: null));
        ctx.SetActivityResult(nameof(SaveAgentSessionActivity), (object?)null);
        ctx.SetActivityResult(nameof(DeleteAgentSessionActivity), (object?)null);
        ctx.SetActivityResult(nameof(AcquireTaskActivity), new AcquireTaskResult("agent/branch"));
        ctx.SetActivityResult(nameof(CreateBranchActivity), new CreateBranchResult("/ws/x", "main"));
        ctx.SetActivityResult(nameof(PlanActivity),
            new PlanResult(AgentRunOutcome.Completed, prNumber, ToolCallsUsed: 5, TerminationReason: null));
        ctx.SetActivityResult(nameof(CreatePullRequestActivity), new CreatePullRequestResult(prNumber));
        ctx.SetActivityResult(nameof(WaitForReviewActivity),
            new WaitForReviewResult(PullRequestReviewState.Pending, false, false, null, DateTimeOffset.UtcNow, Mergeable: null));
        // For the timer-elapsed branch we will eventually need a second poll result —
        // tests configure that via a queue.
        ctx.SetActivityResult(nameof(ModifyCodeActivity),
            new ModifyCodeResult(AgentRunOutcome.Completed, 3, null));
        ctx.SetActivityResult(nameof(MergePullRequestActivity),
            new MergePullRequestResult(MergeOutcome.Merged));
        ctx.SetActivityResult(nameof(DoneActivity), (object?)null);
    }

    // ── PR-number propagation (Step-33 regression) ───────────────────────────

    [Fact]
    public async Task Workflow_passes_plan_pr_number_to_CreatePullRequestActivity()
    {
        // The PR number is known only from PlanActivity's result; the activity must receive
        // it via its input rather than re-reading a volatile cache that never holds it.
        var ctx = new FakeWorkflowContext();
        SetupHappyPathUntilReviewLoop(ctx, prNumber: 77);
        ctx.CompleteExternalEvent("ReadyToMerge", new ReviewEventPayload(77));

        var workflow = new DeveloperTaskWorkflow();
        await workflow.RunAsync(ctx, Input());

        var createPrCall = ctx.ActivityCalls.First(c => c.Name == nameof(CreatePullRequestActivity));
        ((CreatePullRequestActivityInput)createPrCall.Input!).PullRequestNumber.Should().Be(77);
    }

    // ── ReadyToMerge external event ──────────────────────────────────────────

    [Fact]
    public async Task Workflow_merges_then_completes_Done_when_ReadyToMerge_event_arrives()
    {
        var ctx = new FakeWorkflowContext();
        SetupHappyPathUntilReviewLoop(ctx);
        ctx.SetReviewPollResults(
            new WaitForReviewResult(PullRequestReviewState.Pending, false, false, null, DateTimeOffset.UtcNow, Mergeable: null));
        ctx.CompleteExternalEvent("ReadyToMerge", new ReviewEventPayload(7));

        var workflow = new DeveloperTaskWorkflow();
        var result = await workflow.RunAsync(ctx, Input());

        ctx.ActivityCalls.Should().Contain(c => c.Name == nameof(MergePullRequestActivity));
        result.Outcome.Should().Be("Done");
        var doneCall = ctx.ActivityCalls.Last(c => c.Name == nameof(DoneActivity));
        ((DoneActivityInput)doneCall.Input!).Success.Should().BeTrue();
    }

    // ── Direct-poll merge gate ───────────────────────────────────────────────

    [Fact]
    public async Task Workflow_merges_on_direct_poll_when_approved_green_and_mergeable()
    {
        var ctx = new FakeWorkflowContext();
        SetupHappyPathUntilReviewLoop(ctx);
        ctx.SetReviewPollResults(
            new WaitForReviewResult(PullRequestReviewState.Approved, false, true, null, DateTimeOffset.UtcNow, Mergeable: true));

        var workflow = new DeveloperTaskWorkflow();
        var result = await workflow.RunAsync(ctx, Input());

        ctx.ActivityCalls.Should().Contain(c => c.Name == nameof(MergePullRequestActivity));
        var mergeCall = ctx.ActivityCalls.First(c => c.Name == nameof(MergePullRequestActivity));
        ((MergePullRequestActivityInput)mergeCall.Input!).BranchName.Should().Be("agent/branch");
        result.Outcome.Should().Be("Done");
    }

    [Fact]
    public async Task Workflow_keeps_polling_when_approved_but_checks_pending()
    {
        var ctx = new FakeWorkflowContext();
        SetupHappyPathUntilReviewLoop(ctx);
        ctx.SetReviewPollResults(
            // First poll: approved but checks not green yet → must NOT merge, must loop.
            new WaitForReviewResult(PullRequestReviewState.Approved, false, false, null, DateTimeOffset.UtcNow, Mergeable: null),
            // Second poll (after timer): now green + mergeable → merge + Done.
            new WaitForReviewResult(PullRequestReviewState.Approved, false, true, null, DateTimeOffset.UtcNow, Mergeable: true));
        ctx.AutoCompleteTimers = true;

        var workflow = new DeveloperTaskWorkflow();
        var result = await workflow.RunAsync(ctx, Input());

        ctx.ActivityCalls.Count(c => c.Name == nameof(WaitForReviewActivity)).Should().BeGreaterThanOrEqualTo(2);
        ctx.ActivityCalls.Should().Contain(c => c.Name == nameof(MergePullRequestActivity));
        result.Outcome.Should().Be("Done");
    }

    [Fact]
    public async Task Workflow_leaves_item_in_review_and_reports_MergeFailed_when_not_mergeable()
    {
        var ctx = new FakeWorkflowContext();
        SetupHappyPathUntilReviewLoop(ctx);
        ctx.SetActivityResult(nameof(MergePullRequestActivity),
            new MergePullRequestResult(MergeOutcome.NotMergeable));
        ctx.SetReviewPollResults(
            new WaitForReviewResult(PullRequestReviewState.Approved, false, true, null, DateTimeOffset.UtcNow, Mergeable: true));

        var workflow = new DeveloperTaskWorkflow();
        var result = await workflow.RunAsync(ctx, Input());

        result.Outcome.Should().Be("MergeFailed");
        var doneCall = ctx.ActivityCalls.Last(c => c.Name == nameof(DoneActivity));
        var doneInput = (DoneActivityInput)doneCall.Input!;
        doneInput.Success.Should().BeFalse();
        doneInput.PullRequestNumber.Should().Be(7); // non-null PR ⇒ DoneActivity leaves the item In-review
    }

    // ── ChangesRequested external event → modify ─────────────────────────────

    [Fact]
    public async Task Workflow_invokes_ModifyCodeActivity_when_ChangesRequested_event_arrives()
    {
        var ctx = new FakeWorkflowContext();
        SetupHappyPathUntilReviewLoop(ctx);
        ctx.SetReviewPollResults(
            // First poll → Pending so the race arms; ChangesRequested event wins below.
            new WaitForReviewResult(PullRequestReviewState.Pending, false, false, null, DateTimeOffset.UtcNow, Mergeable: null),
            // After ModifyCodeActivity loops back: second poll → Approved+green+mergeable so workflow merges.
            new WaitForReviewResult(PullRequestReviewState.Approved, false, true, null, DateTimeOffset.UtcNow, Mergeable: true));
        // Only ChangesRequested queued — ReadyToMerge event task remains pending → ChangesRequested wins
        // deterministically.
        ctx.CompleteExternalEvent("ChangesRequested", new ReviewEventPayload(7));

        var workflow = new DeveloperTaskWorkflow();
        var result = await workflow.RunAsync(ctx, Input());

        ctx.ActivityCalls.Should().Contain(c => c.Name == nameof(ModifyCodeActivity));
        result.Outcome.Should().Be("Done");
    }

    // ── Timer fallback (cadence driver) ──────────────────────────────────────

    [Fact]
    public async Task Workflow_calls_WaitForReviewActivity_again_when_timer_elapses()
    {
        var ctx = new FakeWorkflowContext();
        SetupHappyPathUntilReviewLoop(ctx);
        ctx.SetReviewPollResults(
            // First poll → Pending; the race fires; only the timer is completed (no events queued).
            new WaitForReviewResult(PullRequestReviewState.Pending, false, false, null, DateTimeOffset.UtcNow, Mergeable: null),
            // Second poll (after timer) → Approved+green+mergeable: the workflow uses the direct merge branch.
            new WaitForReviewResult(PullRequestReviewState.Approved, false, true, null, DateTimeOffset.UtcNow, Mergeable: true));
        ctx.AutoCompleteTimers = true;

        var workflow = new DeveloperTaskWorkflow();
        var result = await workflow.RunAsync(ctx, Input());

        // WaitForReviewActivity should have been called at least twice (initial poll + after-timer poll).
        ctx.ActivityCalls.Count(c => c.Name == nameof(WaitForReviewActivity))
            .Should().BeGreaterThanOrEqualTo(2);
        result.Outcome.Should().Be("Done");
    }

    // ── Recovery fast-path (RecoveryAlreadyMerged) ──────────────────────────

    [Fact]
    public async Task Workflow_with_RecoveryAlreadyMerged_jumps_to_DoneActivity_success()
    {
        var ctx = new FakeWorkflowContext();
        ctx.SetActivityResult(nameof(LoadAgentSessionActivity),
            new AgentSession(Environment.MachineName, "PVTI_x", string.Empty,
                new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero), Summary: null));
        ctx.SetActivityResult(nameof(SaveAgentSessionActivity), (object?)null);
        ctx.SetActivityResult(nameof(DeleteAgentSessionActivity), (object?)null);
        ctx.SetActivityResult(nameof(DoneActivity), (object?)null);

        var input = new TaskInput("PVTI_x", "I_x", 99, "merged out of band", "")
        {
            MaxRetryAttempts = 3,
            FirstRetryIntervalSeconds = 2,
            RecoveryAlreadyMerged = true,
            RecoveryPullRequestNumber = 12,
            RecoveryBranchName = "agent/old-branch"
        };

        var workflow = new DeveloperTaskWorkflow();
        var result = await workflow.RunAsync(ctx, input);

        result.Outcome.Should().Be("Done");

        // Step-18 added Load/Save/Delete around the recovery DoneActivity call.
        var doneCall = ctx.ActivityCalls.Single(c => c.Name == nameof(DoneActivity));
        var doneInput = (DoneActivityInput)doneCall.Input!;
        doneInput.Success.Should().BeTrue();
        doneInput.PullRequestNumber.Should().Be(12);

        // No other primary activity (AcquireTaskActivity, PlanActivity, etc.) should have been called.
        ctx.ActivityCalls.Should().NotContain(c => c.Name == nameof(AcquireTaskActivity));
        ctx.ActivityCalls.Should().NotContain(c => c.Name == nameof(PlanActivity));
    }
}

// FakeWorkflowContext was promoted to its own file in Step-18 so it can be reused
// by DeveloperTaskWorkflowSavesAgentSessionTests. See Workflow/FakeWorkflowContext.cs.
