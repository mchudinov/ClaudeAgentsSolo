using Dapr.Actors;
using Dapr.Actors.Runtime;
using DeveloperAgent.Actors;
using DeveloperAgent.Lifecycle;

namespace DeveloperAgent.Tests.Actors;

/// <summary>
/// Unit tests for <see cref="ProgrammingTaskActor"/>. The actor is created via
/// <see cref="ActorHost.CreateForTest{TActor}(ActorTestOptions)"/> and its
/// <see cref="Actor.StateManager"/> is replaced with an NSubstitute mock so the
/// tests can drive every code path without a live Dapr runtime.
/// </summary>
public sealed class ProgrammingTaskActorTests
{
    private const string StateName = ProgrammingTaskActor.StateKey;

    private static ProgrammingTaskActor CreateActor(
        IActorStateManager stateManager,
        string itemId = "item-1")
    {
        var host = ActorHost.CreateForTest<ProgrammingTaskActor>(
            new ActorTestOptions { ActorId = new ActorId(itemId) });
        var actor = new ProgrammingTaskActor(host);
        actor.SetStateManagerForTesting(stateManager);
        return actor;
    }

    private static IActorStateManager NewStateManager()
    {
        var sm = Substitute.For<IActorStateManager>();
        var store = new Dictionary<string, ProgrammingTaskState>();

        sm.TryGetStateAsync<ProgrammingTaskState>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var name = call.Arg<string>();
                return store.TryGetValue(name, out var value)
                    ? new ConditionalValue<ProgrammingTaskState>(true, value)
                    : new ConditionalValue<ProgrammingTaskState>(false, default!);
            });

        sm.SetStateAsync(Arg.Any<string>(), Arg.Any<ProgrammingTaskState>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                store[call.Arg<string>()] = call.Arg<ProgrammingTaskState>();
                return Task.CompletedTask;
            });

        return sm;
    }

    // ── TryClaimAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task TryClaim_on_empty_state_succeeds_and_persists_agent_id()
    {
        var sm = NewStateManager();
        var actor = CreateActor(sm);

        var claimed = await actor.TryClaimAsync("agent-A");

        claimed.Should().BeTrue();
        var state = await actor.GetStateAsync();
        state.AgentId.Should().Be("agent-A");
        state.Phase.Should().Be(TaskPhase.Acquired);
    }

    [Fact]
    public async Task TryClaim_by_same_agent_is_idempotent()
    {
        var sm = NewStateManager();
        var actor = CreateActor(sm);

        var first = await actor.TryClaimAsync("agent-A");
        var second = await actor.TryClaimAsync("agent-A");

        first.Should().BeTrue();
        second.Should().BeTrue();
    }

    [Fact]
    public async Task TryClaim_by_different_agent_when_already_claimed_returns_false()
    {
        var sm = NewStateManager();
        var actor = CreateActor(sm);

        var first = await actor.TryClaimAsync("agent-A");
        var second = await actor.TryClaimAsync("agent-B");

        first.Should().BeTrue();
        second.Should().BeFalse();
    }

    // ── SetPhaseAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task SetPhase_persists_new_phase()
    {
        var sm = NewStateManager();
        var actor = CreateActor(sm);
        await actor.TryClaimAsync("agent-A");

        await actor.SetPhaseAsync(TaskPhase.WorkspaceReady);

        var state = await actor.GetStateAsync();
        state.Phase.Should().Be(TaskPhase.WorkspaceReady);
    }

    [Fact]
    public async Task SetPhase_to_same_value_is_idempotent()
    {
        var sm = NewStateManager();
        var actor = CreateActor(sm);
        await actor.TryClaimAsync("agent-A");

        await actor.SetPhaseAsync(TaskPhase.AgentRunning);
        await actor.SetPhaseAsync(TaskPhase.AgentRunning);

        var state = await actor.GetStateAsync();
        state.Phase.Should().Be(TaskPhase.AgentRunning);
    }

    // ── SaveBranchAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task SaveBranch_persists_branch_name()
    {
        var sm = NewStateManager();
        var actor = CreateActor(sm);
        await actor.TryClaimAsync("agent-A");

        await actor.SaveBranchAsync("agent/step-10-foo");

        var state = await actor.GetStateAsync();
        state.BranchName.Should().Be("agent/step-10-foo");
    }

    [Fact]
    public async Task SaveBranch_overwrites_previous_value()
    {
        var sm = NewStateManager();
        var actor = CreateActor(sm);
        await actor.TryClaimAsync("agent-A");

        await actor.SaveBranchAsync("agent/first");
        await actor.SaveBranchAsync("agent/second");

        var state = await actor.GetStateAsync();
        state.BranchName.Should().Be("agent/second");
    }

    // ── SavePullRequestAsync ────────────────────────────────────────────────

    [Fact]
    public async Task SavePullRequest_persists_pr_number()
    {
        var sm = NewStateManager();
        var actor = CreateActor(sm);
        await actor.TryClaimAsync("agent-A");

        await actor.SavePullRequestAsync(42);

        var state = await actor.GetStateAsync();
        state.PullRequestNumber.Should().Be(42);
    }

    // ── Mark*Async approval transitions ─────────────────────────────────────

    [Fact]
    public async Task MarkWaitingForReview_sets_phase_and_status()
    {
        var sm = NewStateManager();
        var actor = CreateActor(sm);
        await actor.TryClaimAsync("agent-A");

        await actor.MarkWaitingForReviewAsync();

        var state = await actor.GetStateAsync();
        state.Phase.Should().Be(TaskPhase.AwaitingReview);
        state.ApprovalStatus.Should().Be(ApprovalStatus.WaitingForReview);
    }

    [Fact]
    public async Task MarkApproved_sets_status_to_approved()
    {
        var sm = NewStateManager();
        var actor = CreateActor(sm);
        await actor.TryClaimAsync("agent-A");
        await actor.MarkWaitingForReviewAsync();

        await actor.MarkApprovedAsync();

        var state = await actor.GetStateAsync();
        state.ApprovalStatus.Should().Be(ApprovalStatus.Approved);
    }

    [Fact]
    public async Task MarkChangesRequested_sets_status_and_resets_phase_to_agent_running()
    {
        var sm = NewStateManager();
        var actor = CreateActor(sm);
        await actor.TryClaimAsync("agent-A");
        await actor.MarkWaitingForReviewAsync();

        await actor.MarkChangesRequestedAsync();

        var state = await actor.GetStateAsync();
        state.ApprovalStatus.Should().Be(ApprovalStatus.ChangesRequested);
        state.Phase.Should().Be(TaskPhase.AgentRunning);
    }

    // ── RecordRetryAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task RecordRetry_increments_count_each_call()
    {
        var sm = NewStateManager();
        var actor = CreateActor(sm);
        await actor.TryClaimAsync("agent-A");

        await actor.RecordRetryAsync();
        await actor.RecordRetryAsync();
        await actor.RecordRetryAsync();

        var state = await actor.GetStateAsync();
        state.RetryCount.Should().Be(3);
    }

    [Fact]
    public async Task RecordRetry_returns_new_count()
    {
        var sm = NewStateManager();
        var actor = CreateActor(sm);
        await actor.TryClaimAsync("agent-A");

        var first = await actor.RecordRetryAsync();
        var second = await actor.RecordRetryAsync();

        first.Should().Be(1);
        second.Should().Be(2);
    }

    // ── GetStateAsync on a fresh actor ──────────────────────────────────────

    [Fact]
    public async Task GetState_on_unclaimed_actor_returns_empty_state_with_actor_id()
    {
        var sm = NewStateManager();
        var actor = CreateActor(sm, itemId: "item-99");

        var state = await actor.GetStateAsync();

        state.Should().NotBeNull();
        state.ProjectItemId.Should().Be("item-99");
        state.Phase.Should().Be(TaskPhase.Acquired);
        state.AgentId.Should().BeNull();
        state.BranchName.Should().BeNull();
        state.PullRequestNumber.Should().BeNull();
        state.RetryCount.Should().Be(0);
        state.ApprovalStatus.Should().Be(ApprovalStatus.None);
    }

    // ── Persistence: every mutating call writes to StateManager ─────────────

    [Fact]
    public async Task Mutating_calls_write_to_state_manager()
    {
        var sm = NewStateManager();
        var actor = CreateActor(sm);

        await actor.TryClaimAsync("agent-A");
        await actor.SetPhaseAsync(TaskPhase.WorkspaceReady);
        await actor.SaveBranchAsync("agent/foo");
        await actor.SavePullRequestAsync(7);
        await actor.MarkWaitingForReviewAsync();
        await actor.MarkApprovedAsync();
        await actor.RecordRetryAsync();

        // Seven mutating calls → at least seven SetStateAsync invocations
        // (each writes the composite state record under the single key).
        await sm.Received(7).SetStateAsync(
            StateName,
            Arg.Any<ProgrammingTaskState>(),
            Arg.Any<CancellationToken>());
    }
}
