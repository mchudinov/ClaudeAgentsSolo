using Dapr.Workflow;
using DeveloperAgent.Workflow.Activities;

namespace DeveloperAgent.Workflow;

/// <summary>
/// Dapr Workflow that drives the full lifecycle of a single GitHub project item.
/// </summary>
/// <remarks>
/// Workflow instance ID convention: <c>github-project-item-{itemId}</c>.
/// The dispatcher (Step-14, P2-D part 2/3) is responsible for scheduling the workflow
/// with <c>DaprWorkflowClient.ScheduleNewWorkflowAsync(nameof(DeveloperTaskWorkflow),
/// instanceId: $"github-project-item-{input.ProjectItemId}", input: input)</c>.
///
/// The activity sequence below is a placeholder. P2-D part 2/3 will migrate the logic
/// from <see cref="DeveloperAgent.Lifecycle.TaskExecutor"/> into each individual activity.
/// </remarks>
public sealed class DeveloperTaskWorkflow : Workflow<TaskInput, TaskResult>
{
    public override async Task<TaskResult> RunAsync(WorkflowContext context, TaskInput input)
    {
        // Placeholder sequence — P2-D part 2/3 migrates TaskExecutor logic here.
        // Workflow instance ID = "github-project-item-{itemId}" (set by dispatcher).
        await context.CallActivityAsync<object?>(nameof(AcquireTaskActivity), input);
        await context.CallActivityAsync<object?>(nameof(CreateBranchActivity), input);
        await context.CallActivityAsync<object?>(nameof(PlanActivity), input);
        await context.CallActivityAsync<object?>(nameof(ModifyCodeActivity), input);
        await context.CallActivityAsync<object?>(nameof(BuildActivity), input);
        await context.CallActivityAsync<object?>(nameof(TestActivity), input);
        await context.CallActivityAsync<object?>(nameof(CreatePullRequestActivity), input);
        await context.CallActivityAsync<object?>(nameof(WaitForReviewActivity), input);
        await context.CallActivityAsync<object?>(nameof(DoneActivity), input);
        await context.CallActivityAsync<object?>(nameof(CompactMemoryActivity), input);
        return new TaskResult("Done");
    }
}
