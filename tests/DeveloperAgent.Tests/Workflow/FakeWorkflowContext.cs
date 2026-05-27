using Dapr.Workflow;
using DeveloperAgent.Workflow.Activities;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Tests.Workflow;

/// <summary>
/// Minimal in-process fake of <see cref="WorkflowContext"/> used to test the workflow
/// branching deterministically without a Dapr sidecar. Tests configure canned activity
/// results, queued sequences of activity results, completed external-event payloads, and
/// an opt-in flag to auto-complete timers.
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
