using Dapr.Workflow;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Runs the test suite and reports failures back to the agent.
/// Currently a no-op stub — tests are run by the agent internally via its shell tools.
/// This activity is a future hook for an out-of-band test step.
/// </summary>
public sealed class TestActivity : WorkflowActivity<TaskInput, object?>
{
    private readonly ILogger<TestActivity> _logger;

    public TestActivity(ILogger<TestActivity> logger) => _logger = logger;

    public override Task<object?> RunAsync(WorkflowActivityContext context, TaskInput input)
    {
        _logger.LogDebug("[{Activity}] no-op stub. item={ItemId}", nameof(TestActivity), input.ProjectItemId);
        return Task.FromResult<object?>(null);
    }
}
