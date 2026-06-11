using Dapr.Workflow;
using DeveloperAgent.Workflow;
using DeveloperAgent.Workflow.Activities;
using System.Text.Json;

namespace DeveloperAgent.Tests.Workflow;

/// <summary>
/// Compile-time / reflection-based tests that verify the DeveloperTaskWorkflow skeleton is
/// correctly structured. These tests do not require a live Dapr runtime — they assert on
/// type hierarchy and JSON round-trip only.
/// </summary>
public sealed class DeveloperTaskWorkflowRegistrationTests
{
    // ── Workflow type hierarchy ──────────────────────────────────────────────

    [Fact]
    public void DeveloperTaskWorkflow_is_assignable_to_Workflow_of_TaskInput_TaskResult()
    {
        typeof(DeveloperTaskWorkflow)
            .Should()
            .BeAssignableTo<Workflow<TaskInput, TaskResult>>(
                because: "DeveloperTaskWorkflow must extend Workflow<TaskInput, TaskResult> " +
                         "for Dapr to dispatch it");
    }

    // ── Activity type hierarchy ──────────────────────────────────────────────

    [Theory]
    [InlineData(typeof(TriageActivity))]
    [InlineData(typeof(AcquireTaskActivity))]
    [InlineData(typeof(CreateBranchActivity))]
    [InlineData(typeof(PlanActivity))]
    [InlineData(typeof(ModifyCodeActivity))]
    [InlineData(typeof(BuildActivity))]
    [InlineData(typeof(TestActivity))]
    [InlineData(typeof(CreatePullRequestActivity))]
    [InlineData(typeof(WaitForReviewActivity))]
    [InlineData(typeof(MergePullRequestActivity))]
    [InlineData(typeof(DoneActivity))]
    [InlineData(typeof(CompactMemoryActivity))]
    [InlineData(typeof(LoadAgentSessionActivity))]
    [InlineData(typeof(SaveAgentSessionActivity))]
    [InlineData(typeof(DeleteAgentSessionActivity))]
    public void Activity_extends_WorkflowActivity_open_generic(Type activityType)
    {
        // Each activity uses its own specific typed input/output, so we check the open generic base
        // rather than WorkflowActivity<TaskInput, object?> (generics are invariant).
        var baseType = activityType.BaseType;
        var extendsWorkflowActivity = baseType is not null
            && baseType.IsGenericType
            && baseType.GetGenericTypeDefinition() == typeof(WorkflowActivity<,>);

        extendsWorkflowActivity.Should().BeTrue(
            because: $"{activityType.Name} must extend WorkflowActivity<TInput, TOutput> " +
                     "so that Dapr can discover and invoke it");
    }

    // ── Model record shapes ──────────────────────────────────────────────────

    [Fact]
    public void TaskInput_has_ProjectItemId_property()
    {
        typeof(TaskInput)
            .GetProperty(nameof(TaskInput.ProjectItemId))
            .Should()
            .NotBeNull(because: "TaskInput must expose ProjectItemId for workflow dispatch");
    }

    [Fact]
    public void TaskInput_has_ContentNodeId_property()
    {
        typeof(TaskInput)
            .GetProperty(nameof(TaskInput.ContentNodeId))
            .Should()
            .NotBeNull(because: "TaskInput must expose ContentNodeId");
    }

    [Fact]
    public void TaskInput_has_ContentNumber_property()
    {
        typeof(TaskInput)
            .GetProperty(nameof(TaskInput.ContentNumber))
            .Should()
            .NotBeNull(because: "TaskInput must expose ContentNumber");
    }

    [Fact]
    public void TaskInput_has_Title_property()
    {
        typeof(TaskInput)
            .GetProperty(nameof(TaskInput.Title))
            .Should()
            .NotBeNull(because: "TaskInput must expose Title");
    }

    [Fact]
    public void TaskResult_has_Outcome_property()
    {
        typeof(TaskResult)
            .GetProperty(nameof(TaskResult.Outcome))
            .Should()
            .NotBeNull(because: "TaskResult must expose Outcome for workflow completion tracking");
    }

    // ── JSON round-trip (Dapr uses STJ for workflow state) ───────────────────

    [Fact]
    public void TaskInput_round_trips_through_SystemTextJson()
    {
        var original = new TaskInput(
            ProjectItemId: "PVTI_abc123",
            ContentNodeId: "I_kgDO...",
            ContentNumber: 42,
            Title: "Implement feature X");

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<TaskInput>(json);

        deserialized.Should().Be(original,
            because: "Dapr serializes workflow state via System.Text.Json; " +
                     "TaskInput must survive a round-trip");
    }

    [Fact]
    public void TaskResult_round_trips_through_SystemTextJson()
    {
        var original = new TaskResult("Done");

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<TaskResult>(json);

        deserialized.Should().Be(original,
            because: "Dapr serializes workflow output via System.Text.Json; " +
                     "TaskResult must survive a round-trip");
    }

    // ── All 10 activity types are present in the DeveloperAgent assembly ─────

    [Fact]
    public void All_ten_activity_types_exist_in_DeveloperAgent_assembly()
    {
        var assembly = typeof(DeveloperTaskWorkflow).Assembly;

        var expectedActivityNames = new[]
        {
            "AcquireTaskActivity",
            "CreateBranchActivity",
            "PlanActivity",
            "ModifyCodeActivity",
            "BuildActivity",
            "TestActivity",
            "CreatePullRequestActivity",
            "WaitForReviewActivity",
            "MergePullRequestActivity",
            "DoneActivity",
            "CompactMemoryActivity"
        };

        foreach (var name in expectedActivityNames)
        {
            var type = assembly.GetTypes()
                .SingleOrDefault(t => t.Name == name);

            type.Should().NotBeNull(
                because: $"Activity class '{name}' must exist in the DeveloperAgent assembly. " +
                         "Step-13 requires exactly 10 placeholder activity stubs.");
        }
    }
}
