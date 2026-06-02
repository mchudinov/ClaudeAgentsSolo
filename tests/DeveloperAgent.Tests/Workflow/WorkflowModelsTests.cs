using System.Text.Json;
using DeveloperAgent.Agent;
using DeveloperAgent.GitHub;
using DeveloperAgent.Workflow;

namespace DeveloperAgent.Tests.Workflow;

/// <summary>
/// Verifies that all per-activity input and result records in WorkflowModels.cs are
/// JSON-serializable via System.Text.Json (Dapr serializes workflow state via STJ).
/// </summary>
public sealed class WorkflowModelsTests
{
    private static T RoundTrip<T>(T value)
    {
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<T>(json)!;
    }

    [Fact]
    public void TaskInput_round_trips_with_BodyMarkdown()
    {
        var original = new TaskInput("PVTI_abc", "I_node", 42, "My Task", "body text");
        RoundTrip(original).Should().Be(original);
    }

    [Fact]
    public void TaskResult_round_trips()
    {
        var original = new TaskResult("Done");
        RoundTrip(original).Should().Be(original);
    }

    [Fact]
    public void AcquireTaskActivityInput_round_trips()
    {
        var original = new AcquireTaskActivityInput("PVTI_abc", "I_node", 42, "My Task", "body");
        RoundTrip(original).Should().Be(original);
    }

    [Fact]
    public void CreateBranchActivityInput_round_trips()
    {
        var original = new CreateBranchActivityInput("PVTI_abc", "I_node", 42, "My Task", "body", "agent/branch-abcd1234");
        RoundTrip(original).Should().Be(original);
    }

    [Fact]
    public void PlanActivityInput_round_trips()
    {
        var original = new PlanActivityInput("PVTI_abc", "I_node", 42, "My Task", "body", "agent/branch", "/workspace/path", "main");
        RoundTrip(original).Should().Be(original);
    }

    [Fact]
    public void ModifyCodeActivityInput_round_trips()
    {
        var original = new ModifyCodeActivityInput(
            "PVTI_abc", "I_node", 42, "My Task", "body", "agent/branch",
            "/workspace/path", "main", 99, "Please fix the tests", DateTimeOffset.UtcNow);
        RoundTrip(original).Should().Be(original);
    }

    [Fact]
    public void CreatePullRequestActivityInput_round_trips()
    {
        var original = new CreatePullRequestActivityInput("PVTI_abc", "I_node", 42, "My Task", "agent/branch", 99);
        RoundTrip(original).Should().Be(original);
    }

    [Fact]
    public void WaitForReviewActivityInput_round_trips()
    {
        var original = new WaitForReviewActivityInput("PVTI_abc", 99);
        RoundTrip(original).Should().Be(original);
    }

    [Fact]
    public void DoneActivityInput_round_trips()
    {
        var original = new DoneActivityInput("PVTI_abc", "I_node", "/workspace", "agent/branch", "main", 99, true, 25L);
        RoundTrip(original).Should().Be(original);
    }

    [Fact]
    public void AcquireTaskResult_round_trips()
    {
        var original = new AcquireTaskResult("agent/branch-abcd1234");
        RoundTrip(original).Should().Be(original);
    }

    [Fact]
    public void CreateBranchResult_round_trips()
    {
        var original = new CreateBranchResult("/workspace/item-1/repo", "main");
        RoundTrip(original).Should().Be(original);
    }

    [Fact]
    public void PlanResult_round_trips()
    {
        var original = new PlanResult(AgentRunOutcome.Completed, 42, 15L, null);
        RoundTrip(original).Should().Be(original);
    }

    [Fact]
    public void PlanResult_with_null_PR_round_trips()
    {
        var original = new PlanResult(AgentRunOutcome.HardCapReached, null, 5L, "Hard cap hit");
        RoundTrip(original).Should().Be(original);
    }

    [Fact]
    public void ModifyCodeResult_round_trips()
    {
        var original = new ModifyCodeResult(AgentRunOutcome.Completed, 10L, null);
        RoundTrip(original).Should().Be(original);
    }

    [Fact]
    public void CreatePullRequestResult_round_trips()
    {
        var original = new CreatePullRequestResult(99);
        RoundTrip(original).Should().Be(original);
    }

    [Fact]
    public void WaitForReviewResult_round_trips()
    {
        var polledAt = DateTimeOffset.UtcNow;
        var original = new WaitForReviewResult(PullRequestReviewState.Approved, true, true, null, polledAt);
        RoundTrip(original).Should().Be(original);
    }

    [Fact]
    public void WaitForReviewResult_with_feedback_round_trips()
    {
        var polledAt = DateTimeOffset.UtcNow;
        var original = new WaitForReviewResult(PullRequestReviewState.ChangesRequested, false, false, "Fix the null check", polledAt);
        RoundTrip(original).Should().Be(original);
    }
}
