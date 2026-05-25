using Dapr.Actors;
using Dapr.Actors.Client;
using DeveloperAgent.Actors;
using DeveloperAgent.Lifecycle;

namespace DeveloperAgent.Tests.Lifecycle;

/// <summary>
/// Unit tests for <see cref="DaprActorTaskStateStore"/>. The store is exercised through
/// an NSubstitute-faked <see cref="IActorProxyFactory"/> that hands back a substituted
/// <see cref="IProgrammingTaskActor"/>, so no live Dapr runtime is required.
/// </summary>
public sealed class DaprActorTaskStateStoreTests
{
    private const string AgentId = "test-agent";

    private static TaskState MakeState(
        string projectItemId = "item-1",
        TaskPhase phase = TaskPhase.Acquired,
        string? branchName = null,
        int? pullRequestNumber = null,
        string? lastError = null) =>
        new(
            ProjectItemId: projectItemId,
            IssueNumber: 7,
            Title: "Test task",
            Phase: phase,
            BranchName: branchName,
            PullRequestNumber: pullRequestNumber,
            LastError: lastError,
            StartedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            PullRequestOpenedAtUtc: null,
            LastReviewPolledAtUtc: null);

    private static (DaprActorTaskStateStore store, IProgrammingTaskActor actor, IActorProxyFactory factory)
        CreateStore(string agentId = AgentId)
    {
        var actor = Substitute.For<IProgrammingTaskActor>();
        actor.TryClaimAsync(Arg.Any<string>()).Returns(true);
        var factory = Substitute.For<IActorProxyFactory>();
        factory
            .CreateActorProxy<IProgrammingTaskActor>(
                Arg.Any<ActorId>(),
                Arg.Any<string>(),
                Arg.Any<ActorProxyOptions?>())
            .Returns(actor);
        var store = new DaprActorTaskStateStore(factory, agentId);
        return (store, actor, factory);
    }

    // ── Current ──────────────────────────────────────────────────────────────

    [Fact]
    public void Empty_store_returns_null()
    {
        var (store, _, _) = CreateStore();

        store.Current.Should().BeNull();
    }

    // ── Set: first call claims and persists phase ────────────────────────────

    [Fact]
    public void Set_first_call_claims_actor_and_persists_phase()
    {
        var (store, actor, factory) = CreateStore();
        var state = MakeState("item-42", TaskPhase.Acquired);

        store.Set(state);

        // Resolved a proxy for the project item id, of the actor type matching the
        // implementation registered in Step-10.
        factory.Received(1).CreateActorProxy<IProgrammingTaskActor>(
            Arg.Is<ActorId>(id => id.GetId() == "item-42"),
            nameof(ProgrammingTaskActor),
            Arg.Any<ActorProxyOptions?>());

        actor.Received(1).TryClaimAsync(AgentId);
        actor.Received(1).SetPhaseAsync(TaskPhase.Acquired);
    }

    [Fact]
    public void Set_throws_when_claim_is_lost_to_another_agent()
    {
        var actor = Substitute.For<IProgrammingTaskActor>();
        actor.TryClaimAsync(Arg.Any<string>()).Returns(false);
        var factory = Substitute.For<IActorProxyFactory>();
        factory.CreateActorProxy<IProgrammingTaskActor>(
                Arg.Any<ActorId>(),
                Arg.Any<string>(),
                Arg.Any<ActorProxyOptions?>())
            .Returns(actor);
        var store = new DaprActorTaskStateStore(factory, AgentId);

        var act = () => store.Set(MakeState("item-claimed-elsewhere"));

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*item-claimed-elsewhere*");
    }

    // ── Set: cache reflects last value (Current returns it) ─────────────────

    [Fact]
    public void Set_then_Current_returns_set_value()
    {
        var (store, _, _) = CreateStore();
        var state = MakeState("item-1");

        store.Set(state);

        store.Current.Should().BeSameAs(state);
    }

    [Fact]
    public void Current_preserves_volatile_fields_that_the_actor_does_not_persist()
    {
        var (store, _, _) = CreateStore();
        var state = MakeState("item-1", lastError: "boom") with
        {
            StartedAtUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            UpdatedAtUtc = DateTimeOffset.Parse("2026-01-01T00:05:00Z"),
            PullRequestOpenedAtUtc = DateTimeOffset.Parse("2026-01-01T00:04:00Z"),
            LastReviewPolledAtUtc = DateTimeOffset.Parse("2026-01-01T00:04:30Z"),
        };

        store.Set(state);

        // Volatile fields survive in the cache even though the actor has no slots for them.
        store.Current!.LastError.Should().Be("boom");
        store.Current!.IssueNumber.Should().Be(7);
        store.Current!.Title.Should().Be("Test task");
        store.Current!.PullRequestOpenedAtUtc.Should().Be(DateTimeOffset.Parse("2026-01-01T00:04:00Z"));
        store.Current!.LastReviewPolledAtUtc.Should().Be(DateTimeOffset.Parse("2026-01-01T00:04:30Z"));
    }

    // ── Set: only dispatches actor methods for fields that changed ─────────

    [Fact]
    public void Set_dispatches_SaveBranchAsync_when_branch_first_assigned()
    {
        var (store, actor, _) = CreateStore();
        var initial = MakeState("item-1", TaskPhase.Acquired);
        var withBranch = MakeState(
            "item-1", TaskPhase.WorkspaceReady, branchName: "agent/foo");

        store.Set(initial);
        actor.ClearReceivedCalls();

        store.Set(withBranch);

        actor.Received(1).SetPhaseAsync(TaskPhase.WorkspaceReady);
        actor.Received(1).SaveBranchAsync("agent/foo");
        actor.DidNotReceive().SavePullRequestAsync(Arg.Any<int>());
    }

    [Fact]
    public void Set_does_not_redispatch_SaveBranchAsync_when_branch_unchanged()
    {
        var (store, actor, _) = CreateStore();
        var s1 = MakeState("item-1", TaskPhase.WorkspaceReady, branchName: "agent/foo");
        var s2 = MakeState("item-1", TaskPhase.AgentRunning, branchName: "agent/foo");

        store.Set(s1);
        actor.ClearReceivedCalls();

        store.Set(s2);

        actor.Received(1).SetPhaseAsync(TaskPhase.AgentRunning);
        actor.DidNotReceive().SaveBranchAsync(Arg.Any<string>());
    }

    [Fact]
    public void Set_dispatches_SavePullRequestAsync_when_PR_first_assigned()
    {
        var (store, actor, _) = CreateStore();
        var initial = MakeState(
            "item-1", TaskPhase.AgentRunning, branchName: "agent/foo");
        var withPr = MakeState(
            "item-1", TaskPhase.PullRequestOpen,
            branchName: "agent/foo", pullRequestNumber: 99);

        store.Set(initial);
        actor.ClearReceivedCalls();

        store.Set(withPr);

        actor.Received(1).SetPhaseAsync(TaskPhase.PullRequestOpen);
        actor.Received(1).SavePullRequestAsync(99);
        actor.DidNotReceive().SaveBranchAsync(Arg.Any<string>());
    }

    [Fact]
    public void Set_to_AwaitingReview_calls_MarkWaitingForReviewAsync()
    {
        var (store, actor, _) = CreateStore();
        var initial = MakeState(
            "item-1", TaskPhase.PullRequestOpen,
            branchName: "agent/foo", pullRequestNumber: 99);
        var awaitingReview = MakeState(
            "item-1", TaskPhase.AwaitingReview,
            branchName: "agent/foo", pullRequestNumber: 99);

        store.Set(initial);
        actor.ClearReceivedCalls();

        store.Set(awaitingReview);

        // The MarkWaitingForReviewAsync path subsumes SetPhaseAsync, so the explicit
        // SetPhaseAsync call is skipped when the transition is exactly to AwaitingReview.
        actor.Received(1).MarkWaitingForReviewAsync();
        actor.DidNotReceive().SetPhaseAsync(TaskPhase.AwaitingReview);
    }

    // ── Set: switching project item triggers a fresh claim ─────────────────

    [Fact]
    public void Set_for_a_different_project_item_claims_again_on_the_new_actor()
    {
        var actorA = Substitute.For<IProgrammingTaskActor>();
        actorA.TryClaimAsync(Arg.Any<string>()).Returns(true);
        var actorB = Substitute.For<IProgrammingTaskActor>();
        actorB.TryClaimAsync(Arg.Any<string>()).Returns(true);

        var factory = Substitute.For<IActorProxyFactory>();
        factory.CreateActorProxy<IProgrammingTaskActor>(
                Arg.Is<ActorId>(id => id.GetId() == "item-A"),
                Arg.Any<string>(),
                Arg.Any<ActorProxyOptions?>())
            .Returns(actorA);
        factory.CreateActorProxy<IProgrammingTaskActor>(
                Arg.Is<ActorId>(id => id.GetId() == "item-B"),
                Arg.Any<string>(),
                Arg.Any<ActorProxyOptions?>())
            .Returns(actorB);

        var store = new DaprActorTaskStateStore(factory, AgentId);

        store.Set(MakeState("item-A"));
        store.Set(MakeState("item-B"));

        actorA.Received(1).TryClaimAsync(AgentId);
        actorB.Received(1).TryClaimAsync(AgentId);
    }

    // ── Clear ───────────────────────────────────────────────────────────────

    [Fact]
    public void Clear_resets_cache_only_and_does_not_call_any_actor_method()
    {
        var (store, actor, _) = CreateStore();
        store.Set(MakeState("item-1"));
        actor.ClearReceivedCalls();

        store.Clear();

        store.Current.Should().BeNull();
        // Clear is a replica-local cursor reset; durable actor state is intentionally
        // left intact so another replica (or a restart) can still inspect it.
        actor.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public void Clear_then_Set_for_same_item_reclaims_on_the_actor()
    {
        var (store, actor, _) = CreateStore();
        store.Set(MakeState("item-1"));
        actor.ClearReceivedCalls();

        store.Clear();
        store.Set(MakeState("item-1"));

        // Re-claim is idempotent on the actor side (returns true for same agent),
        // but DaprActorTaskStateStore must issue the claim again because its cached
        // "currently claimed" cursor was reset by Clear().
        actor.Received(1).TryClaimAsync(AgentId);
    }

    // ── Constructor argument validation ────────────────────────────────────

    [Fact]
    public void Ctor_rejects_null_factory()
    {
        var act = () => new DaprActorTaskStateStore(null!, AgentId);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_rejects_empty_agent_id()
    {
        var factory = Substitute.For<IActorProxyFactory>();

        var act = () => new DaprActorTaskStateStore(factory, "");

        act.Should().Throw<ArgumentException>();
    }
}
