using Agent.Workflow;
using Dapr.Workflow;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Workflow.Tests;

public sealed class WorkflowInspectorServiceCollectionExtensionsTests
{
    [Fact]
    public void AddWorkflowInspector_registers_inspector_as_singleton()
    {
        var services = new ServiceCollection();

        services.AddWorkflowInspector();

        services.Should().ContainSingle(d =>
            d.ServiceType == typeof(IWorkflowInstanceInspector)
            && d.ImplementationType == typeof(DaprWorkflowInstanceInspector)
            && d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddWorkflowInspector_throws_on_null_services()
    {
        var act = () => ((IServiceCollection)null!).AddWorkflowInspector();

        act.Should().Throw<ArgumentNullException>();
    }
}

public sealed class DaprWorkflowInstanceInspectorTests
{
    [Fact]
    public async Task GetDispositionAsync_returns_NotFound_when_no_state_exists()
    {
        // Dapr returns a null/absent state for an unknown id; the inspector maps that to NotFound
        // (the only mapping branch reachable in a unit test — WorkflowState has no public ctor,
        // which is exactly why the inspector exists as a seam).
        var client = Substitute.For<IDaprWorkflowClient>();
        client.GetWorkflowStateAsync(Arg.Any<string>(), cancellation: Arg.Any<CancellationToken>())
              .ReturnsForAnyArgs(Task.FromResult<WorkflowState?>(null));

        var inspector = new DaprWorkflowInstanceInspector(client);

        var disposition = await inspector.GetDispositionAsync("github-project-item-42", CancellationToken.None);

        disposition.Should().Be(WorkflowInstanceDisposition.NotFound);
    }
}

public sealed class WorkflowInstanceDispositionTests
{
    [Fact]
    public void NotFound_is_the_default_zero_value()
    {
        // NotFound must be 0 so the "no record → safe to schedule" case is the default.
        ((int)WorkflowInstanceDisposition.NotFound).Should().Be(0);
        Enum.GetValues<WorkflowInstanceDisposition>().Should()
            .BeEquivalentTo(new[]
            {
                WorkflowInstanceDisposition.NotFound,
                WorkflowInstanceDisposition.Active,
                WorkflowInstanceDisposition.Terminal,
            });
    }
}
