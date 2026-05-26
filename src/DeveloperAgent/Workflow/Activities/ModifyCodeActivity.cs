using Dapr.Workflow;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Invokes the agent loop to modify code according to the plan.
/// Placeholder — P2-D part 2/3 will migrate TaskExecutor logic here.
/// </summary>
public sealed class ModifyCodeActivity : WorkflowActivity<TaskInput, object?>
{
    private readonly ILogger<ModifyCodeActivity> _logger;

    public ModifyCodeActivity(ILogger<ModifyCodeActivity> logger) => _logger = logger;

    public override Task<object?> RunAsync(WorkflowActivityContext context, TaskInput input)
    {
        // placeholder — P2-D part 2/3 will migrate TaskExecutor logic here
        _logger.LogInformation("[{Activity}] item={ItemId}", nameof(ModifyCodeActivity), input.ProjectItemId);
        return Task.FromResult<object?>(null);
    }
}
