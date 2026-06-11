using Dapr.Workflow;
using DeveloperAgent.Agent;
using DeveloperAgent.GitHub;
using DeveloperAgent.Workflow;
using DeveloperAgent.Workflow.Activities;

namespace DeveloperAgent.Tests.Workflow;

/// <summary>
/// Verifies that every phase activity is invoked with a non-null retry policy
/// whose <c>MaxNumberOfAttempts</c> matches the value carried in <see cref="TaskInput"/>.
/// </summary>
public sealed class RetryPolicyTests
{
    private const int ExpectedMaxAttempts = 5;
    private const int ExpectedFirstRetrySec = 3;

    private static TaskInput Input() =>
        new("PVTI_abc", "I_node", 42, "Add feature X", "Body")
        {
            MaxRetryAttempts = ExpectedMaxAttempts,
            FirstRetryIntervalSeconds = ExpectedFirstRetrySec
        };

    private static void PopulateHappyPathResults(FakeWorkflowContext ctx, int prNumber = 7)
    {
        // Step-18: AgentSession persistence activities also run on the happy path.
        ctx.SetActivityResult(nameof(LoadAgentSessionActivity),
            new AgentSession(Environment.MachineName, "PVTI_abc", string.Empty,
                new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero), Summary: null));
        ctx.SetActivityResult(nameof(SaveAgentSessionActivity), (object?)null);
        ctx.SetActivityResult(nameof(DeleteAgentSessionActivity), (object?)null);
        ctx.SetActivityResult(nameof(AcquireTaskActivity), new AcquireTaskResult("agent/branch"));
        ctx.SetActivityResult(nameof(CreateBranchActivity), new CreateBranchResult("/ws/x", "main"));
        ctx.SetActivityResult(nameof(PlanActivity),
            new PlanResult(AgentRunOutcome.Completed, prNumber, 5, null));
        ctx.SetActivityResult(nameof(CreatePullRequestActivity), new CreatePullRequestResult(prNumber));
        ctx.SetActivityResult(nameof(WaitForReviewActivity),
            new WaitForReviewResult(PullRequestReviewState.Approved, true, true, null, DateTimeOffset.UtcNow, Mergeable: true));
        ctx.SetActivityResult(nameof(MergePullRequestActivity),
            new MergePullRequestResult(MergeOutcome.Merged));
        ctx.SetActivityResult(nameof(DoneActivity), (object?)null);
    }

    [Theory]
    [InlineData(nameof(AcquireTaskActivity))]
    [InlineData(nameof(CreateBranchActivity))]
    [InlineData(nameof(PlanActivity))]
    [InlineData(nameof(CreatePullRequestActivity))]
    [InlineData(nameof(WaitForReviewActivity))]
    [InlineData(nameof(DoneActivity))]
    public async Task CallActivityAsync_is_invoked_with_retry_policy_having_bound_MaxAttempts(string activityName)
    {
        var ctx = new FakeWorkflowContext();
        PopulateHappyPathResults(ctx);
        // The happy-path poll result is Approved+Merged so the direct success branch fires —
        // no external event needed.

        var workflow = new DeveloperTaskWorkflow();
        _ = await workflow.RunAsync(ctx, Input());

        var call = ctx.ActivityCalls.FirstOrDefault(c => c.Name == activityName);
        call.Should().NotBeNull(because: $"{activityName} should have been called on the happy path");
        call!.Options.Should().NotBeNull(because: $"{activityName} must be invoked with WorkflowTaskOptions");
        call.Options!.RetryPolicy.Should().NotBeNull(because: $"{activityName} must have a retry policy");
        call.Options.RetryPolicy!.MaxNumberOfAttempts.Should().Be(ExpectedMaxAttempts);
        call.Options.RetryPolicy.FirstRetryInterval.Should().Be(TimeSpan.FromSeconds(ExpectedFirstRetrySec));
    }

    [Fact]
    public async Task ModifyCodeActivity_is_invoked_with_retry_policy_having_bound_MaxAttempts()
    {
        var ctx = new FakeWorkflowContext();
        PopulateHappyPathResults(ctx);
        // First poll returns ChangesRequested → direct ModifyCode call → second poll Approved+green+mergeable.
        ctx.SetReviewPollResults(
            new WaitForReviewResult(PullRequestReviewState.ChangesRequested, false, false, "fix", DateTimeOffset.UtcNow, Mergeable: null),
            new WaitForReviewResult(PullRequestReviewState.Approved, false, true, null, DateTimeOffset.UtcNow, Mergeable: true));
        ctx.SetActivityResult(nameof(ModifyCodeActivity),
            new ModifyCodeResult(AgentRunOutcome.Completed, 3, null));

        var workflow = new DeveloperTaskWorkflow();
        _ = await workflow.RunAsync(ctx, Input());

        var modifyCall = ctx.ActivityCalls.FirstOrDefault(c => c.Name == nameof(ModifyCodeActivity));
        modifyCall.Should().NotBeNull();
        modifyCall!.Options.Should().NotBeNull();
        modifyCall.Options!.RetryPolicy.Should().NotBeNull();
        modifyCall.Options.RetryPolicy!.MaxNumberOfAttempts.Should().Be(ExpectedMaxAttempts);
    }
}
