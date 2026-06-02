using Dapr.Workflow;

namespace DeveloperAgent.Workflow;

/// <summary>
/// <see cref="IWorkflowInstanceInspector"/> backed by <see cref="IDaprWorkflowClient.GetWorkflowStateAsync"/>.
/// Maps the un-constructible <see cref="WorkflowState"/> onto the plain
/// <see cref="WorkflowInstanceDisposition"/> the lifecycle service reasons over.
/// </summary>
/// <remarks>
/// <see cref="IDaprWorkflowClient.GetWorkflowStateAsync"/> does not throw for an unknown id; it
/// returns a state whose <see cref="WorkflowState.Exists"/> is <c>false</c>. The terminal set
/// mirrors the Dapr workflow engine's completed states — once an instance reaches any of them its
/// record lingers under the deterministic id and must be purged before reuse.
/// </remarks>
internal sealed class DaprWorkflowInstanceInspector(IDaprWorkflowClient client) : IWorkflowInstanceInspector
{
    public async Task<WorkflowInstanceDisposition> GetDispositionAsync(string instanceId, CancellationToken ct)
    {
        var state = await client.GetWorkflowStateAsync(instanceId, cancellation: ct);
        if (state is null || !state.Exists)
            return WorkflowInstanceDisposition.NotFound;

        return state.RuntimeStatus switch
        {
            WorkflowRuntimeStatus.Completed
                or WorkflowRuntimeStatus.Failed
                or WorkflowRuntimeStatus.Terminated
                or WorkflowRuntimeStatus.Canceled => WorkflowInstanceDisposition.Terminal,
            _ => WorkflowInstanceDisposition.Active
        };
    }
}
