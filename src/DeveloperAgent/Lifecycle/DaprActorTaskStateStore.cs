using Dapr.Actors;
using Dapr.Actors.Client;
using DeveloperAgent.Actors;

namespace DeveloperAgent.Lifecycle;

/// <summary>
/// Durable <see cref="ITaskStateStore"/> backed by <see cref="ProgrammingTaskActor"/>.
/// Replaces <see cref="InMemoryTaskStateStore"/> for production: per-item phase, branch
/// and PR number survive a process restart, and only one replica can hold a given
/// project item's claim at a time (the LLD §Dapr Actors invariant).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ITaskStateStore"/> is fully synchronous because its read site (<c>/info</c>)
/// and its write sites (<see cref="TaskExecutor"/>) were authored against an in-process
/// store. This class therefore uses a <b>write-through cache</b>:
/// </para>
/// <list type="bullet">
/// <item><see cref="Current"/> returns the cached <see cref="TaskState"/> — cheap, no
///   sidecar round-trip, safe for concurrent reads from any thread.</item>
/// <item><see cref="Set"/> updates the cache and synchronously dispatches the actor
///   methods for the fields the actor knows about (<c>Phase</c>, <c>BranchName</c>,
///   <c>PullRequestNumber</c>, approval transitions). Sync-over-async via
///   <c>GetAwaiter().GetResult()</c> surfaces actor errors immediately.</item>
/// <item><see cref="Clear"/> only resets the cache. The actor's durable state is
///   intentionally left in place so another replica (or a restart) can still observe
///   it — that is the "recovery" hook P2-C will use.</item>
/// </list>
/// <para>
/// <b>Field coverage.</b> <see cref="TaskState"/> holds volatile diagnostic data
/// (<c>IssueNumber</c>, <c>Title</c>, <c>LastError</c>, timestamps) that has no slot in
/// the actor's <see cref="ProgrammingTaskState"/>. Those fields survive only in the cache.
/// A restart loses them; <c>Phase</c>, <c>BranchName</c> and <c>PullRequestNumber</c>
/// are the durable subset that matters for resume.
/// </para>
/// <para>
/// <b>Delta dispatch.</b> <see cref="Set"/> diffs the new state against the previous
/// cached value and only invokes actor methods for fields that actually changed. This
/// keeps the per-call sidecar traffic minimal (often one round trip for the phase
/// transition) and matches the LLD's "small stateful actions" semantics.
/// </para>
/// </remarks>
public sealed class DaprActorTaskStateStore : ITaskStateStore
{
    private readonly IActorProxyFactory _proxyFactory;
    private readonly string _agentId;
    private readonly Lock _lock = new();

    private TaskState? _current;
    private string? _claimedProjectItemId;

    /// <summary>
    /// Creates a new store. <paramref name="agentId"/> is the identity persisted into
    /// <see cref="ProgrammingTaskState.AgentId"/> via <see cref="IProgrammingTaskActor.TryClaimAsync"/>
    /// — typically <c>Environment.MachineName</c> so two containers can be distinguished.
    /// </summary>
    public DaprActorTaskStateStore(IActorProxyFactory proxyFactory, string agentId)
    {
        ArgumentNullException.ThrowIfNull(proxyFactory);
        ArgumentException.ThrowIfNullOrEmpty(agentId);

        _proxyFactory = proxyFactory;
        _agentId = agentId;
    }

    /// <inheritdoc/>
    public TaskState? Current
    {
        get
        {
            lock (_lock)
                return _current;
        }
    }

    /// <inheritdoc/>
    public void Set(TaskState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        TaskState? previous;
        lock (_lock)
        {
            previous = _current;
            _current = state;
        }

        var actor = _proxyFactory.CreateActorProxy<IProgrammingTaskActor>(
            new ActorId(state.ProjectItemId),
            nameof(ProgrammingTaskActor));

        var isNewItem = previous is null || previous.ProjectItemId != state.ProjectItemId;
        if (isNewItem)
        {
            ClaimOrThrow(actor, state.ProjectItemId);
            lock (_lock)
            {
                _claimedProjectItemId = state.ProjectItemId;
            }
        }

        // ── Phase ─────────────────────────────────────────────────────────────
        // AwaitingReview is special: the actor exposes MarkWaitingForReviewAsync,
        // which also flips ApprovalStatus. Use it when we observe the transition
        // so the actor's composite state stays consistent with the LLD.
        var phaseChanged = isNewItem || previous!.Phase != state.Phase;
        if (phaseChanged)
        {
            if (state.Phase == TaskPhase.AwaitingReview)
            {
                actor.MarkWaitingForReviewAsync().GetAwaiter().GetResult();
            }
            else
            {
                actor.SetPhaseAsync(state.Phase).GetAwaiter().GetResult();
            }
        }

        // ── Branch ────────────────────────────────────────────────────────────
        var branchChanged = isNewItem || previous!.BranchName != state.BranchName;
        if (branchChanged && !string.IsNullOrEmpty(state.BranchName))
        {
            actor.SaveBranchAsync(state.BranchName).GetAwaiter().GetResult();
        }

        // ── Pull request ──────────────────────────────────────────────────────
        var prChanged = isNewItem || previous!.PullRequestNumber != state.PullRequestNumber;
        if (prChanged && state.PullRequestNumber is int prNumber)
        {
            actor.SavePullRequestAsync(prNumber).GetAwaiter().GetResult();
        }
    }

    /// <inheritdoc/>
    public void Clear()
    {
        lock (_lock)
        {
            _current = null;
            _claimedProjectItemId = null;
        }
    }

    private void ClaimOrThrow(IProgrammingTaskActor actor, string projectItemId)
    {
        var claimed = actor.TryClaimAsync(_agentId).GetAwaiter().GetResult();
        if (!claimed)
        {
            throw new InvalidOperationException(
                $"Project item {projectItemId} is already claimed by a different agent — cannot Set TaskState.");
        }
    }
}
