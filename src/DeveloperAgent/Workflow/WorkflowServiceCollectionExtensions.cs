using Dapr.Workflow;
using DeveloperAgent.Workflow.Activities;
using Microsoft.Extensions.DependencyInjection;

namespace DeveloperAgent.Workflow;

/// <summary>
/// Registers <see cref="DeveloperTaskWorkflow"/> and its activities with the Dapr Workflow
/// runtime, plus the DI plumbing the rest of the app needs to schedule workflow instances.
/// </summary>
public static class WorkflowServiceCollectionExtensions
{
    /// <summary>
    /// Registers the developer-task workflow, all its activities, and exposes the workflow
    /// client as <see cref="IDaprWorkflowClient"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="WorkflowServiceCollectionExtensions"/> centralises this so the production host
    /// (<c>Program.cs</c>) and the registration regression test share one source of truth.
    ///
    /// <c>AddDaprWorkflow</c> registers the concrete <see cref="DaprWorkflowClient"/> in DI but
    /// does <b>not</b> register the <see cref="IDaprWorkflowClient"/> interface. Our consumers —
    /// <see cref="DeveloperAgent.Lifecycle.AgentLifecycleService"/> and
    /// <see cref="DeveloperAgent.Workflow.Activities.WaitForReviewActivity"/> — inject the
    /// interface (so they stay unit-testable with a mock). Without the bridge below the host
    /// fails its <c>ValidateOnBuild</c> check at startup with
    /// "Unable to resolve service for type 'Dapr.Workflow.IDaprWorkflowClient'".
    /// </remarks>
    public static IServiceCollection AddDeveloperTaskWorkflow(this IServiceCollection services)
    {
        services.AddDaprWorkflow(opt =>
        {
            opt.RegisterWorkflow<DeveloperTaskWorkflow>();
            opt.RegisterActivity<AcquireTaskActivity>();
            opt.RegisterActivity<CreateBranchActivity>();
            opt.RegisterActivity<PlanActivity>();
            opt.RegisterActivity<ModifyCodeActivity>();
            opt.RegisterActivity<BuildActivity>();
            opt.RegisterActivity<TestActivity>();
            opt.RegisterActivity<CreatePullRequestActivity>();
            opt.RegisterActivity<WaitForReviewActivity>();
            opt.RegisterActivity<DoneActivity>();
            opt.RegisterActivity<CompactMemoryActivity>();
            // Step-18: AgentSession persistence activities.
            opt.RegisterActivity<LoadAgentSessionActivity>();
            opt.RegisterActivity<SaveAgentSessionActivity>();
            opt.RegisterActivity<DeleteAgentSessionActivity>();
        });

        // Bridge IDaprWorkflowClient → the concrete DaprWorkflowClient that AddDaprWorkflow
        // registered. See the remarks above: our consumers depend on the interface.
        services.AddSingleton<IDaprWorkflowClient>(sp => sp.GetRequiredService<DaprWorkflowClient>());

        // Idempotent-scheduling seam: AgentLifecycleService asks this whether a deterministic
        // workflow instance id is free, already active, or terminal before scheduling.
        services.AddSingleton<IWorkflowInstanceInspector, DaprWorkflowInstanceInspector>();

        return services;
    }
}
