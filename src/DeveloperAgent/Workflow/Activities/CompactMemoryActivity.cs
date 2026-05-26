using Dapr.Workflow;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Invokes /compact on the agent's conversation to trim token usage before the next cycle.
/// Currently a no-op stub — P2-J will implement this.
/// </summary>
public sealed class CompactMemoryActivity : WorkflowActivity<TaskInput, object?>
{
    private readonly ILogger<CompactMemoryActivity> _logger;

    public CompactMemoryActivity(ILogger<CompactMemoryActivity> logger) => _logger = logger;

    public override Task<object?> RunAsync(WorkflowActivityContext context, TaskInput input)
    {
        _logger.LogDebug("[{Activity}] no-op stub. item={ItemId}", nameof(CompactMemoryActivity), input.ProjectItemId);
        return Task.FromResult<object?>(null);
    }
}
