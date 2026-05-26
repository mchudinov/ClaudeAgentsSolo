using Dapr.Workflow;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Acquires the GitHub project item and transitions it to InProgress.
/// Placeholder — P2-D part 2/3 will migrate TaskExecutor logic here.
/// </summary>
public sealed class AcquireTaskActivity : WorkflowActivity<TaskInput, object?>
{
    private readonly ILogger<AcquireTaskActivity> _logger;

    public AcquireTaskActivity(ILogger<AcquireTaskActivity> logger) => _logger = logger;

    public override Task<object?> RunAsync(WorkflowActivityContext context, TaskInput input)
    {
        // placeholder — P2-D part 2/3 will migrate TaskExecutor logic here
        _logger.LogInformation("[{Activity}] item={ItemId}", nameof(AcquireTaskActivity), input.ProjectItemId);
        return Task.FromResult<object?>(null);
    }
}
