using Dapr.Workflow;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Runs the test suite and reports failures back to the agent.
/// Placeholder — P2-D part 2/3 will migrate TaskExecutor logic here.
/// </summary>
public sealed class TestActivity : WorkflowActivity<TaskInput, object?>
{
    private readonly ILogger<TestActivity> _logger;

    public TestActivity(ILogger<TestActivity> logger) => _logger = logger;

    public override Task<object?> RunAsync(WorkflowActivityContext context, TaskInput input)
    {
        // placeholder — P2-D part 2/3 will migrate TaskExecutor logic here
        _logger.LogInformation("[{Activity}] item={ItemId}", nameof(TestActivity), input.ProjectItemId);
        return Task.FromResult<object?>(null);
    }
}
