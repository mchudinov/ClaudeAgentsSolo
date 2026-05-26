using Dapr.Workflow;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Opens a GitHub pull request and transitions the project item to InReview.
/// Placeholder — P2-D part 2/3 will migrate TaskExecutor logic here.
/// </summary>
public sealed class CreatePullRequestActivity : WorkflowActivity<TaskInput, object?>
{
    private readonly ILogger<CreatePullRequestActivity> _logger;

    public CreatePullRequestActivity(ILogger<CreatePullRequestActivity> logger) => _logger = logger;

    public override Task<object?> RunAsync(WorkflowActivityContext context, TaskInput input)
    {
        // placeholder — P2-D part 2/3 will migrate TaskExecutor logic here
        _logger.LogInformation("[{Activity}] item={ItemId}", nameof(CreatePullRequestActivity), input.ProjectItemId);
        return Task.FromResult<object?>(null);
    }
}
