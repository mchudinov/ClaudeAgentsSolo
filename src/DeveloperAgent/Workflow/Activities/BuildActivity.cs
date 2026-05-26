using Dapr.Workflow;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Runs a build check and reports compile errors back to the agent.
/// Currently a no-op stub — build is run by the agent internally via its shell tools.
/// This activity is a future hook for an out-of-band build step.
/// </summary>
public sealed class BuildActivity : WorkflowActivity<TaskInput, object?>
{
    private readonly ILogger<BuildActivity> _logger;

    public BuildActivity(ILogger<BuildActivity> logger) => _logger = logger;

    public override Task<object?> RunAsync(WorkflowActivityContext context, TaskInput input)
    {
        _logger.LogDebug("[{Activity}] no-op stub. item={ItemId}", nameof(BuildActivity), input.ProjectItemId);
        return Task.FromResult<object?>(null);
    }
}
