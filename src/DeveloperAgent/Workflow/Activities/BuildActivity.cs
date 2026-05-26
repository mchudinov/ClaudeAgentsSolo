using Dapr.Workflow;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Runs the build and reports compile errors back to the agent.
/// Placeholder — P2-D part 2/3 will migrate TaskExecutor logic here.
/// </summary>
public sealed class BuildActivity : WorkflowActivity<TaskInput, object?>
{
    private readonly ILogger<BuildActivity> _logger;

    public BuildActivity(ILogger<BuildActivity> logger) => _logger = logger;

    public override Task<object?> RunAsync(WorkflowActivityContext context, TaskInput input)
    {
        // placeholder — P2-D part 2/3 will migrate TaskExecutor logic here
        _logger.LogInformation("[{Activity}] item={ItemId}", nameof(BuildActivity), input.ProjectItemId);
        return Task.FromResult<object?>(null);
    }
}
