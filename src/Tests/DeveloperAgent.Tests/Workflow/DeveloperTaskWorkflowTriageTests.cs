using DeveloperAgent.Agent;
using DeveloperAgent.GitHub;
using DeveloperAgent.Workflow;
using DeveloperAgent.Workflow.Activities;

namespace DeveloperAgent.Tests.Workflow;

/// <summary>
/// Seam-level tests for the Step-54 triage gate in <see cref="DeveloperTaskWorkflow"/>. Uses the
/// shared <see cref="FakeWorkflowContext"/> to assert routing: a not-relevant verdict stops the
/// workflow with outcome "Rejected" (no Acquire), while a relevant verdict proceeds normally.
/// </summary>
public sealed class DeveloperTaskWorkflowTriageTests
{
    private static TaskInput Input(string itemId = "PVTI_abc") =>
        new(itemId, "I_node", 42, "Add feature X", "Body")
        {
            MaxRetryAttempts = 3,
            FirstRetryIntervalSeconds = 2,
        };

    private static AgentSession FreshSession(string itemId = "PVTI_abc") =>
        new(Environment.MachineName, itemId, string.Empty,
            new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero), Summary: null);

    private static void SetupHappyPath(FakeWorkflowContext ctx, int prNumber = 7)
    {
        ctx.SetActivityResult(nameof(LoadAgentSessionActivity), FreshSession());
        ctx.SetActivityResult(nameof(SaveAgentSessionActivity), (object?)null);
        ctx.SetActivityResult(nameof(DeleteAgentSessionActivity), (object?)null);
        ctx.SetActivityResult(nameof(AcquireTaskActivity), new AcquireTaskResult("agent/branch"));
        ctx.SetActivityResult(nameof(CreateBranchActivity), new CreateBranchResult("/ws/x", "main"));
        ctx.SetActivityResult(nameof(PlanActivity),
            new PlanResult(AgentRunOutcome.Completed, prNumber, ToolCallsUsed: 5, TerminationReason: null));
        ctx.SetActivityResult(nameof(CreatePullRequestActivity), new CreatePullRequestResult(prNumber));
        ctx.SetActivityResult(nameof(WaitForReviewActivity),
            new WaitForReviewResult(PullRequestReviewState.Approved, true, true, null, DateTimeOffset.UtcNow));
        ctx.SetActivityResult(nameof(DoneActivity), (object?)null);
    }

    // ── Rejection routing ────────────────────────────────────────────────────

    [Fact]
    public async Task Not_relevant_verdict_stops_with_Rejected_and_never_acquires()
    {
        var ctx = new FakeWorkflowContext();
        ctx.SetActivityResult(nameof(LoadAgentSessionActivity), FreshSession());
        ctx.SetActivityResult(nameof(DeleteAgentSessionActivity), (object?)null);
        ctx.SetActivityResult(nameof(TriageActivity), new TriageActivityResult(IsRelevant: false, Reason: "Out of scope."));

        var workflow = new DeveloperTaskWorkflow();
        var result = await workflow.RunAsync(ctx, Input());

        result.Outcome.Should().Be("Rejected");
        ctx.ActivityCalls.Should().NotContain(c => c.Name == nameof(AcquireTaskActivity),
            because: "a rejected item must not be acquired, branched, or planned");
        ctx.ActivityCalls.Should().NotContain(c => c.Name == nameof(PlanActivity));
    }

    [Fact]
    public async Task Rejection_deletes_the_session_at_the_tail()
    {
        var ctx = new FakeWorkflowContext();
        ctx.SetActivityResult(nameof(LoadAgentSessionActivity), FreshSession());
        ctx.SetActivityResult(nameof(DeleteAgentSessionActivity), (object?)null);
        ctx.SetActivityResult(nameof(TriageActivity), new TriageActivityResult(false, "Out of scope."));

        var workflow = new DeveloperTaskWorkflow();
        await workflow.RunAsync(ctx, Input("PVTI_reject"));

        var delete = ctx.ActivityCalls.Single(c => c.Name == nameof(DeleteAgentSessionActivity));
        ((DeleteAgentSessionActivityInput)delete.Input!).ProjectItemId.Should().Be("PVTI_reject");
    }

    [Fact]
    public async Task Triage_runs_after_Load_and_before_Acquire()
    {
        var ctx = new FakeWorkflowContext();
        SetupHappyPath(ctx);
        ctx.SetActivityResult(nameof(TriageActivity), new TriageActivityResult(true, "In scope."));

        var workflow = new DeveloperTaskWorkflow();
        await workflow.RunAsync(ctx, Input());

        var names = ctx.ActivityCalls.Select(c => c.Name).ToList();
        names.Should().ContainInOrder(
            nameof(LoadAgentSessionActivity),
            nameof(TriageActivity),
            nameof(AcquireTaskActivity));
    }

    // ── Relevant routing ─────────────────────────────────────────────────────

    [Fact]
    public async Task Relevant_verdict_proceeds_to_acquire_and_completes()
    {
        var ctx = new FakeWorkflowContext();
        SetupHappyPath(ctx);
        ctx.SetActivityResult(nameof(TriageActivity), new TriageActivityResult(true, "In scope."));

        var workflow = new DeveloperTaskWorkflow();
        var result = await workflow.RunAsync(ctx, Input());

        ctx.ActivityCalls.Should().Contain(c => c.Name == nameof(AcquireTaskActivity));
        result.Outcome.Should().Be("Done");
    }

    [Fact]
    public async Task Triage_passes_the_item_fields_to_the_activity()
    {
        var ctx = new FakeWorkflowContext();
        SetupHappyPath(ctx);
        ctx.SetActivityResult(nameof(TriageActivity), new TriageActivityResult(true, "ok"));

        var workflow = new DeveloperTaskWorkflow();
        await workflow.RunAsync(ctx, Input());

        var triageCall = ctx.ActivityCalls.Single(c => c.Name == nameof(TriageActivity));
        var triageInput = (TriageActivityInput)triageCall.Input!;
        triageInput.ProjectItemId.Should().Be("PVTI_abc");
        triageInput.Title.Should().Be("Add feature X");
        triageInput.BodyMarkdown.Should().Be("Body");
    }

    // ── Recovery fast-path bypasses triage entirely ──────────────────────────

    [Fact]
    public async Task Recovery_fast_path_does_not_run_triage()
    {
        var ctx = new FakeWorkflowContext();
        ctx.SetActivityResult(nameof(LoadAgentSessionActivity), FreshSession("PVTI_recovery"));
        ctx.SetActivityResult(nameof(SaveAgentSessionActivity), (object?)null);
        ctx.SetActivityResult(nameof(DeleteAgentSessionActivity), (object?)null);
        ctx.SetActivityResult(nameof(DoneActivity), (object?)null);

        var input = new TaskInput("PVTI_recovery", "I_x", 99, "merged out of band", "")
        {
            MaxRetryAttempts = 3,
            FirstRetryIntervalSeconds = 2,
            RecoveryAlreadyMerged = true,
            RecoveryPullRequestNumber = 12,
            RecoveryBranchName = "agent/old-branch",
        };

        var workflow = new DeveloperTaskWorkflow();
        var result = await workflow.RunAsync(ctx, input);

        result.Outcome.Should().Be("Done");
        ctx.ActivityCalls.Should().NotContain(c => c.Name == nameof(TriageActivity),
            because: "a PR merged out-of-band needs no relevance check — recovery returns before triage");
    }
}
