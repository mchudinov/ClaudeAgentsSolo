using Dapr.Workflow;

namespace DeveloperAgent.Workflow;

/// <summary>
/// Disposition of a Dapr workflow instance under a given (deterministic) instance id, as it
/// concerns whether a fresh instance may be scheduled under that id.
/// </summary>
public enum WorkflowInstanceDisposition
{
    /// <summary>No orchestration record exists for the id — safe to schedule a fresh instance.</summary>
    NotFound = 0,

    /// <summary>
    /// A non-terminal instance exists (Running / Pending / Suspended / …). The Dapr runtime
    /// rehydrates and continues it on its own after a restart; scheduling a new instance under the
    /// same id throws <c>an active workflow with ID ... already exists</c>, so callers must not
    /// reschedule it.
    /// </summary>
    Active,

    /// <summary>
    /// A terminal instance exists (Completed / Failed / Terminated / Canceled). Its record still
    /// occupies the deterministic id, so callers must purge it before scheduling a fresh instance.
    /// </summary>
    Terminal
}

/// <summary>
/// Probes Dapr for the <see cref="WorkflowInstanceDisposition"/> of a workflow instance id.
/// <para>
/// This exists as a seam because <see cref="WorkflowState"/> has no public constructor and cannot
/// be instantiated or mocked in unit tests. The schedule-conflict decision in
/// <see cref="DeveloperAgent.Lifecycle.AgentLifecycleService"/> depends on this plain enum so that
/// the decision logic stays fully unit-testable.
/// </para>
/// </summary>
public interface IWorkflowInstanceInspector
{
    /// <summary>
    /// Returns the disposition of the workflow instance identified by <paramref name="instanceId"/>.
    /// </summary>
    Task<WorkflowInstanceDisposition> GetDispositionAsync(string instanceId, CancellationToken ct);
}
