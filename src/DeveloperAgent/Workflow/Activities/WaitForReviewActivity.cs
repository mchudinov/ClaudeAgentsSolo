using Dapr.Workflow;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Polls the GitHub PR for an approval or change-request review verdict.
/// Placeholder — P2-D part 2/3 will migrate TaskExecutor logic here.
/// </summary>
public sealed class WaitForReviewActivity : WorkflowActivity<TaskInput, object?>
{
    private readonly ILogger<WaitForReviewActivity> _logger;

    public WaitForReviewActivity(ILogger<WaitForReviewActivity> logger) => _logger = logger;

    public override Task<object?> RunAsync(WorkflowActivityContext context, TaskInput input)
    {
        // placeholder — P2-D part 2/3 will migrate TaskExecutor logic here
        _logger.LogInformation("[{Activity}] item={ItemId}", nameof(WaitForReviewActivity), input.ProjectItemId);
        return Task.FromResult<object?>(null);
    }
}
