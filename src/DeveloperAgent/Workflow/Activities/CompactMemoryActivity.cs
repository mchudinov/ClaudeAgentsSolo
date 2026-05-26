using Dapr.Workflow;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Invokes /compact on the agent's conversation to trim token usage before the next cycle.
/// Placeholder — P2-D part 2/3 will migrate TaskExecutor logic here.
/// </summary>
public sealed class CompactMemoryActivity : WorkflowActivity<TaskInput, object?>
{
    private readonly ILogger<CompactMemoryActivity> _logger;

    public CompactMemoryActivity(ILogger<CompactMemoryActivity> logger) => _logger = logger;

    public override Task<object?> RunAsync(WorkflowActivityContext context, TaskInput input)
    {
        // placeholder — P2-D part 2/3 will migrate TaskExecutor logic here
        _logger.LogInformation("[{Activity}] item={ItemId}", nameof(CompactMemoryActivity), input.ProjectItemId);
        return Task.FromResult<object?>(null);
    }
}
