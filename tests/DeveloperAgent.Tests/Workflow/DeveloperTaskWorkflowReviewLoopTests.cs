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
        ctx.SetActivityResult(nameof(AcquireTaskActivity), new AcquireTaskResult("agent/branch"));
        ctx.SetActivityResult(nameof(CreateBranchActivity), new CreateBranchResult("/ws/x", "main"));
        ctx.SetActivityResult(nameof(PlanActivity),
            new PlanResult(AgentRunOutcome.Completed, prNumber, ToolCallsUsed: 5, TerminationReason: null));
        ctx.SetActivityResult(nameof(CreatePullRequestActivity), new CreatePullRequestResult(prNumber));
        ctx.SetActivityResult(nameof(WaitForReviewActivity),
            new WaitForReviewResult(PullRequestReviewState.Pending, false, false, null, DateTimeOffset.UtcNow));
        // For the timer-elapsed branch we will eventually need a second poll result —
        // tests configure that via a queue.
        ctx.SetActivityResult(nameof(ModifyCodeActivity),
            new ModifyCodeResult(AgentRunOutcome.Completed, 3, null));
        ctx.SetActivityResult(nameof(DoneActivity), (object?)null);
    }

    // ── Merged external event ────────────────────────────────────────────────

    [Fact]
    public async Task Workflow_completes_with_Done_when_Merged_event_arrives()
    {
        var ctx = new FakeWorkflowContext();
        SetupHappyPathUntilReviewLoop(ctx);
        ctx.SetReviewPollResults(
            new WaitForReviewResult(PullRequestReviewState.Pending, false, false, null, DateTimeOffset.UtcNow),
            new WaitForReviewResult(PullRequestReviewState.Approved, true, true, null, DateTimeOffset.UtcNow));
        // Only Merged event queued — race resolves deterministically on Merged.
        ctx.CompleteExternalEvent("Merged", new ReviewEventPayload(7));

        var workflow = new DeveloperTaskWorkflow();
        var result = await workflow.RunAsync(ctx, Input());

        result.Outcome.Should().Be("Done");
        ctx.ActivityCalls.Should().Contain(c => c.Name == nameof(DoneActivity));
        var doneCall = ctx.ActivityCalls.Last(c => c.Name == nameof(DoneActivity));
        ((DoneActivityInput)doneCall.Input!).Success.Should().BeTrue();
    }

    // ── ChangesRequested external event → modify ─────────────────────────────

    [Fact]
    public async Task Workflow_invokes_ModifyCodeActivity_when_ChangesRequested_event_arrives()
    {
        var ctx = new FakeWorkflowContext();
        SetupHappyPathUntilReviewLoop(ctx);
        ctx.SetReviewPollResults(
            // First poll → Pending so the race arms; ChangesRequested event wins below.
            new WaitForReviewResult(PullRequestReviewState.Pending, false, false, null, DateTimeOffset.UtcNow),
            // After ModifyCodeActivity loops back: second poll → Approved+Merged so workflow exits.
            new WaitForReviewResult(PullRequestReviewState.Approved, true, true, null, DateTimeOffset.UtcNow));
        // Only ChangesRequested queued — Merged event task remains pending → ChangesRequested wins
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
            new WaitForReviewResult(PullRequestReviewState.Pending, false, false, null, DateTimeOffset.UtcNow),
            // Second poll (after timer) → Approved+Merged: the workflow uses the direct success branch.
            new WaitForReviewResult(PullRequestReviewState.Approved, true, true, null, DateTimeOffset.UtcNow));
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
        ctx.ActivityCalls.Should().HaveCount(1);
        var call = ctx.ActivityCalls[0];
        call.Name.Should().Be(nameof(DoneActivity));
        var doneInput = (DoneActivityInput)call.Input!;
        doneInput.Success.Should().BeTrue();
        doneInput.PullRequestNumber.Should().Be(12);

        // No other activity (AcquireTaskActivity, PlanActivity, etc.) should have been called.
        ctx.ActivityCalls.Should().NotContain(c => c.Name == nameof(AcquireTaskActivity));
        ctx.ActivityCalls.Should().NotContain(c => c.Name == nameof(PlanActivity));
    }
}

/// <summary>
/// Minimal in-process fake of <see cref="WorkflowContext"/> used to test the workflow
/// branching deterministically without a Dapr sidecar.
/// </summary>
internal sealed class FakeWorkflowContext : WorkflowContext
{
    public List<ActivityCall> ActivityCalls { get; } = [];
    private readonly Dictionary<string, object?> _activityResults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<object?>> _activityResultQueues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<TaskCompletionSource<object?>>> _eventQueues = new(StringComparer.Ordinal);
    public bool AutoCompleteTimers { get; set; }

    public override string Name => "DeveloperTaskWorkflow";
    public override string InstanceId => "github-project-item-PVTI_abc";
    public override DateTime CurrentUtcDateTime => DateTime.UtcNow;
    public override bool IsReplaying => false;

    public void SetActivityResult(string activityName, object? result) =>
        _activityResults[activityName] = result;

    public void SetReviewPollResults(params object[] results)
    {
        if (!_activityResultQueues.TryGetValue(nameof(WaitForReviewActivity), out var q))
        {
            q = new Queue<object?>();
            _activityResultQueues[nameof(WaitForReviewActivity)] = q;
        }
        foreach (var r in results) q.Enqueue(r);
    }

    public void CompleteExternalEvent(string eventName, object payload)
    {
        if (!_eventQueues.TryGetValue(eventName, out var q))
        {
            q = new Queue<TaskCompletionSource<object?>>();
            _eventQueues[eventName] = q;
        }
        var tcs = new TaskCompletionSource<object?>();
        tcs.SetResult(payload);
        q.Enqueue(tcs);
    }

    public override Task<TResult> CallActivityAsync<TResult>(string name, object? input = null, WorkflowTaskOptions? options = null)
    {
        ActivityCalls.Add(new ActivityCall(name, input, options));

        // Prefer queued result for activities the test queued multiple results for.
        if (_activityResultQueues.TryGetValue(name, out var q) && q.Count > 0)
            return Task.FromResult((TResult)q.Dequeue()!);

        if (_activityResults.TryGetValue(name, out var result))
            return Task.FromResult((TResult)result!);

        return Task.FromResult(default(TResult)!);
    }

    public override Task CallActivityAsync(string name, object? input = null, WorkflowTaskOptions? options = null)
    {
        ActivityCalls.Add(new ActivityCall(name, input, options));
        return Task.CompletedTask;
    }

    public override Task<T> WaitForExternalEventAsync<T>(string eventName, CancellationToken cancellationToken = default)
    {
        if (_eventQueues.TryGetValue(eventName, out var q) && q.Count > 0)
        {
            var tcs = q.Dequeue();
            // Cast the payload to the requested type. For tests using `new { }` the cast may
            // fail at runtime — production code uses ReviewEventPayload which is a real type,
            // so tests should use that record. For simple cases where payload is ignored we
            // return default.
            if (tcs.Task.IsCompletedSuccessfully && tcs.Task.Result is T typed)
                return Task.FromResult(typed);
            // Payload type mismatch — return default(T). Production code does not depend on payload.
            return Task.FromResult<T>(default!);
        }

        // No event queued: never-completing task so Task.WhenAny picks the other arm.
        return new TaskCompletionSource<T>().Task;
    }

    public override Task CreateTimer(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        if (AutoCompleteTimers)
            return Task.CompletedTask;
        // Default: never completes.
        return new TaskCompletionSource().Task;
    }

    public override Task CreateTimer(DateTime fireAt, CancellationToken cancellationToken = default) =>
        CreateTimer(fireAt - DateTime.UtcNow, cancellationToken);

    public override Task<TResult> CallChildWorkflowAsync<TResult>(string workflowName, object? input = null, ChildWorkflowTaskOptions? options = null)
        => throw new NotSupportedException();

    public override Task CallChildWorkflowAsync(string workflowName, object? input = null, ChildWorkflowTaskOptions? options = null)
        => throw new NotSupportedException();

    public override void ContinueAsNew(object? newInput = null, bool preserveUnprocessedEvents = true)
        => throw new NotSupportedException();

    public override Guid NewGuid() => Guid.NewGuid();
    public override bool IsPatched(string patchName) => false;
    public override void SendEvent(string instanceId, string eventName, object payload) { }
    public override void SetCustomStatus(object? customStatus) { }

    public override ILogger CreateReplaySafeLogger(string categoryName)
        => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    public override ILogger CreateReplaySafeLogger(Type type)
        => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    public override ILogger CreateReplaySafeLogger<T>()
        => Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;

    public sealed record ActivityCall(string Name, object? Input, WorkflowTaskOptions? Options);
}
