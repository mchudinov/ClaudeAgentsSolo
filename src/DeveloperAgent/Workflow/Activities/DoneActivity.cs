using Dapr.Workflow;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Moves the GitHub project item to Done after the PR is merged.
/// Placeholder — P2-D part 2/3 will migrate TaskExecutor logic here.
/// </summary>
public sealed class DoneActivity : WorkflowActivity<TaskInput, object?>
{
    private readonly ILogger<DoneActivity> _logger;

    public DoneActivity(ILogger<DoneActivity> logger) => _logger = logger;

    public override Task<object?> RunAsync(WorkflowActivityContext context, TaskInput input)
    {
        // placeholder — P2-D part 2/3 will migrate TaskExecutor logic here
        _logger.LogInformation("[{Activity}] item={ItemId}", nameof(DoneActivity), input.ProjectItemId);
        return Task.FromResult<object?>(null);
    }
}
