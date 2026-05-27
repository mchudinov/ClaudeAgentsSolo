using DeveloperAgent.Agent;
using DeveloperAgent.AgentMemory;
using DeveloperAgent.GitHub;
using DeveloperAgent.Workflow;
using DeveloperAgent.Workflow.Activities;

namespace DeveloperAgent.Tests.Workflow;

/// <summary>
/// Seam-level tests for the Step-18 AgentSession persistence in
/// <see cref="DeveloperTaskWorkflow"/>. Asserts that
/// <see cref="LoadAgentSessionActivity"/> runs once at workflow start and that
/// <see cref="SaveAgentSessionActivity"/> is scheduled after every primary activity
/// with <c>CurrentStep</c> set to the just-finished activity name. Also covers the
/// recovery fast-path (Load + Save around DoneActivity) and the workflow-tail delete.
/// </summary>
public sealed class DeveloperTaskWorkflowSavesAgentSessionTests
{
    private static TaskInput Input(string itemId = "PVTI_abc") =>
        new(itemId, "I_node", 42, "Add feature X", "Body")
        {
            MaxRetryAttempts = 3,
            FirstRetryIntervalSeconds = 2,
        };

    /// <summary>Pre-loads canned activity results so the workflow walks the success path.</summary>
    private static void SetupHappyPath(FakeWorkflowContext ctx, int prNumber = 7)
    {
        ctx.SetActivityResult(nameof(LoadAgentSessionActivity), MakeFreshSession());
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

    private static AgentSession MakeFreshSession(string itemId = "PVTI_abc") =>
        new(
            AgentId: Environment.MachineName,
            ProjectItemId: itemId,
            CurrentStep: string.Empty,
            LastActivityTimestampUtc: new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero),
            Summary: null);

    // ── LoadAgentSessionActivity is invoked once at the start ────────────────

    [Fact]
    public async Task LoadAgentSessionActivity_is_invoked_first_in_the_happy_path()
    {
        var ctx = new FakeWorkflowContext();
        SetupHappyPath(ctx);

        var workflow = new DeveloperTaskWorkflow();
        await workflow.RunAsync(ctx, Input());

        var firstCall = ctx.ActivityCalls.First();
        firstCall.Name.Should().Be(nameof(LoadAgentSessionActivity),
            because: "the workflow must load (or initialise) the AgentSession before any IO-bearing activity");

        var loadInput = firstCall.Input as LoadAgentSessionActivityInput;
        loadInput.Should().NotBeNull();
        loadInput!.ProjectItemId.Should().Be("PVTI_abc");
    }

    [Fact]
    public async Task LoadAgentSessionActivity_runs_only_once()
    {
        var ctx = new FakeWorkflowContext();
        SetupHappyPath(ctx);

        var workflow = new DeveloperTaskWorkflow();
        await workflow.RunAsync(ctx, Input());

        ctx.ActivityCalls.Count(c => c.Name == nameof(LoadAgentSessionActivity))
            .Should().Be(1);
    }

    // ── SaveAgentSessionActivity fires after each primary activity ───────────

    [Fact]
    public async Task SaveAgentSessionActivity_runs_after_AcquireTaskActivity_with_CurrentStep_set()
    {
        var ctx = new FakeWorkflowContext();
        SetupHappyPath(ctx);

        var workflow = new DeveloperTaskWorkflow();
        await workflow.RunAsync(ctx, Input());

        AssertSaveImmediatelyAfter(ctx, nameof(AcquireTaskActivity));
    }

    [Fact]
    public async Task SaveAgentSessionActivity_runs_after_CreateBranchActivity_with_CurrentStep_set()
    {
        var ctx = new FakeWorkflowContext();
        SetupHappyPath(ctx);

        var workflow = new DeveloperTaskWorkflow();
        await workflow.RunAsync(ctx, Input());

        AssertSaveImmediatelyAfter(ctx, nameof(CreateBranchActivity));
    }

    [Fact]
    public async Task SaveAgentSessionActivity_runs_after_PlanActivity_with_CurrentStep_set()
    {
        var ctx = new FakeWorkflowContext();
        SetupHappyPath(ctx);

        var workflow = new DeveloperTaskWorkflow();
        await workflow.RunAsync(ctx, Input());

        AssertSaveImmediatelyAfter(ctx, nameof(PlanActivity));
    }

    [Fact]
    public async Task SaveAgentSessionActivity_runs_after_CreatePullRequestActivity_with_CurrentStep_set()
    {
        var ctx = new FakeWorkflowContext();
        SetupHappyPath(ctx);

        var workflow = new DeveloperTaskWorkflow();
        await workflow.RunAsync(ctx, Input());

        AssertSaveImmediatelyAfter(ctx, nameof(CreatePullRequestActivity));
    }

    [Fact]
    public async Task SaveAgentSessionActivity_runs_after_WaitForReviewActivity_with_CurrentStep_set()
    {
        var ctx = new FakeWorkflowContext();
        SetupHappyPath(ctx);

        var workflow = new DeveloperTaskWorkflow();
        await workflow.RunAsync(ctx, Input());

        AssertSaveImmediatelyAfter(ctx, nameof(WaitForReviewActivity));
    }

    [Fact]
    public async Task SaveAgentSessionActivity_runs_after_DoneActivity_with_CurrentStep_set()
    {
        var ctx = new FakeWorkflowContext();
        SetupHappyPath(ctx);

        var workflow = new DeveloperTaskWorkflow();
        await workflow.RunAsync(ctx, Input());

        AssertSaveImmediatelyAfter(ctx, nameof(DoneActivity));
    }

    [Fact]
    public async Task DeleteAgentSessionActivity_runs_at_the_workflow_tail()
    {
        var ctx = new FakeWorkflowContext();
        SetupHappyPath(ctx);

        var workflow = new DeveloperTaskWorkflow();
        await workflow.RunAsync(ctx, Input());

        ctx.ActivityCalls.Should().Contain(c => c.Name == nameof(DeleteAgentSessionActivity),
            because: "after the terminal DoneActivity the workflow must remove the persisted session " +
                     "so the state store does not accumulate stale entries");

        var delete = ctx.ActivityCalls.Single(c => c.Name == nameof(DeleteAgentSessionActivity));
        ((DeleteAgentSessionActivityInput)delete.Input!).ProjectItemId.Should().Be("PVTI_abc");
    }

    // ── Recovery fast-path also persists ─────────────────────────────────────

    [Fact]
    public async Task Recovery_fast_path_loads_saves_and_deletes_the_session_around_DoneActivity()
    {
        var ctx = new FakeWorkflowContext();
        ctx.SetActivityResult(nameof(LoadAgentSessionActivity), MakeFreshSession("PVTI_recovery"));
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
        await workflow.RunAsync(ctx, input);

        // Order: Load → Done → Save(Done) → Delete.
        var names = ctx.ActivityCalls.Select(c => c.Name).ToArray();
        names.Should().ContainInOrder(
            nameof(LoadAgentSessionActivity),
            nameof(DoneActivity),
            nameof(SaveAgentSessionActivity),
            nameof(DeleteAgentSessionActivity));

        AssertSaveImmediatelyAfter(ctx, nameof(DoneActivity));
    }

    // ── Helper assertion ─────────────────────────────────────────────────────

    /// <summary>
    /// Asserts that <see cref="SaveAgentSessionActivity"/> is invoked immediately after the
    /// activity named <paramref name="activityName"/>, with the just-finished activity's name
    /// recorded as <c>CurrentStep</c>.
    /// </summary>
    private static void AssertSaveImmediatelyAfter(FakeWorkflowContext ctx, string activityName)
    {
        var calls = ctx.ActivityCalls;
        var idx = -1;
        for (var i = 0; i < calls.Count; i++)
        {
            if (calls[i].Name == activityName) { idx = i; break; }
        }

        idx.Should().BeGreaterThanOrEqualTo(0,
            because: $"the workflow must call {activityName} at least once on the happy path");

        idx.Should().BeLessThan(calls.Count - 1,
            because: $"{activityName} must be followed by SaveAgentSessionActivity");

        var next = calls[idx + 1];
        next.Name.Should().Be(nameof(SaveAgentSessionActivity),
            because: $"{activityName} must be immediately followed by SaveAgentSessionActivity, not '{next.Name}'");

        var saveInput = (SaveAgentSessionActivityInput)next.Input!;
        saveInput.CurrentStep.Should().Be(activityName,
            because: $"the persisted CurrentStep must reflect the activity that just completed ({activityName})");
        saveInput.ProjectItemId.Should().NotBeNullOrEmpty(
            because: "the save must target the project item the workflow is processing");
    }
}
