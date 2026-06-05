using Agent.Workflow;
using Dapr.Workflow;
using DeveloperAgent.Workflow;
using Microsoft.Extensions.DependencyInjection;

namespace DeveloperAgent.Tests.Workflow;

/// <summary>
/// Guards the production DI wiring for the Dapr workflow client. <c>AddDaprWorkflow</c> registers
/// the concrete <see cref="DaprWorkflowClient"/> but not the <see cref="IDaprWorkflowClient"/>
/// interface that <see cref="DeveloperAgent.Lifecycle.AgentLifecycleService"/> and
/// <see cref="DeveloperAgent.Workflow.Activities.WaitForReviewActivity"/> inject. Without the
/// bridge in <see cref="WorkflowServiceCollectionExtensions.AddDeveloperTaskWorkflow"/> the host
/// crashes at startup (ValidateOnBuild) — this test reproduces that resolution at unit speed.
/// </summary>
public sealed class WorkflowRegistrationTests
{
    [Fact]
    public void AddDeveloperTaskWorkflow_registers_IDaprWorkflowClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddDeveloperTaskWorkflow();

        // The interface our consumers inject must be resolvable, not just the concrete class.
        services.Any(d => d.ServiceType == typeof(IDaprWorkflowClient))
            .Should().BeTrue("AgentLifecycleService and WaitForReviewActivity inject IDaprWorkflowClient");

        using var provider = services.BuildServiceProvider();
        provider.GetService<IDaprWorkflowClient>()
            .Should().NotBeNull("the host's ValidateOnBuild must be able to construct IDaprWorkflowClient");
    }

    [Fact]
    public void AddDeveloperTaskWorkflow_registers_IWorkflowInstanceInspector()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddDeveloperTaskWorkflow();

        using var provider = services.BuildServiceProvider();
        provider.GetService<IWorkflowInstanceInspector>()
            .Should().NotBeNull(
                "AgentLifecycleService injects IWorkflowInstanceInspector to decide whether a " +
                "deterministic workflow instance id is free, already active, or terminal");
    }
}
