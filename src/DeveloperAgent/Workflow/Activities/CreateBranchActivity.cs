using Dapr.Workflow;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Creates a Git branch for the task and prepares the workspace.
/// Placeholder — P2-D part 2/3 will migrate TaskExecutor logic here.
/// </summary>
public sealed class CreateBranchActivity : WorkflowActivity<TaskInput, object?>
{
    private readonly ILogger<CreateBranchActivity> _logger;

    public CreateBranchActivity(ILogger<CreateBranchActivity> logger) => _logger = logger;

    public override Task<object?> RunAsync(WorkflowActivityContext context, TaskInput input)
    {
        // placeholder — P2-D part 2/3 will migrate TaskExecutor logic here
        _logger.LogInformation("[{Activity}] item={ItemId}", nameof(CreateBranchActivity), input.ProjectItemId);
        return Task.FromResult<object?>(null);
    }
}
