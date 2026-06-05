using Microsoft.Extensions.DependencyInjection;

namespace Agent.Workflow;

/// <summary>
/// DI registration for the agent-neutral Dapr workflow-instance inspector.
/// </summary>
public static class WorkflowInspectorServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IWorkflowInstanceInspector"/> (backed by the internal
    /// <c>DaprWorkflowInstanceInspector</c>) as a singleton.
    /// </summary>
    /// <remarks>
    /// The inspector depends on <c>Dapr.Workflow.IDaprWorkflowClient</c>; the host must register
    /// that itself (e.g. via <c>AddDaprWorkflow</c> plus the interface bridge), exactly as it does
    /// for its own workflow + activities. This extension exists because the concrete inspector is
    /// internal to the library and therefore cannot be wired up by the host directly.
    /// </remarks>
    public static IServiceCollection AddWorkflowInspector(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IWorkflowInstanceInspector, DaprWorkflowInstanceInspector>();

        return services;
    }
}
