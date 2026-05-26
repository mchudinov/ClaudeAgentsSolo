using Dapr.Workflow;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Asks the agent to produce a plan for the task.
/// Placeholder — P2-D part 2/3 will migrate TaskExecutor logic here.
/// </summary>
public sealed class PlanActivity : WorkflowActivity<TaskInput, object?>
{
    private readonly ILogger<PlanActivity> _logger;

    public PlanActivity(ILogger<PlanActivity> logger) => _logger = logger;

    public override Task<object?> RunAsync(WorkflowActivityContext context, TaskInput input)
    {
        // placeholder — P2-D part 2/3 will migrate TaskExecutor logic here
        _logger.LogInformation("[{Activity}] item={ItemId}", nameof(PlanActivity), input.ProjectItemId);
        return Task.FromResult<object?>(null);
    }
}
