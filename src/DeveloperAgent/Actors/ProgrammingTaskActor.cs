using Dapr.Actors.Runtime;
using DeveloperAgent.Lifecycle;

namespace DeveloperAgent.Actors;

/// <summary>
/// Concrete <see cref="IProgrammingTaskActor"/> backed by Dapr actor state. All
/// mutating methods load → mutate → persist the single composite
/// <see cref="ProgrammingTaskState"/> record under <see cref="StateKey"/>.
/// </summary>
public sealed class ProgrammingTaskActor : Actor, IProgrammingTaskActor
{
    /// <summary>Name under which the composite state record is stored.</summary>
    internal const string StateKey = "task-state";

    public ProgrammingTaskActor(ActorHost host) : base(host)
    {
    }

    /// <summary>
    /// Test-only hook to swap in a stub <see cref="IActorStateManager"/>. The
    /// base <see cref="Actor.StateManager"/> setter is <c>protected</c>; this
    /// surface keeps the production API clean while letting unit tests drive
    /// the actor without a live Dapr runtime.
    /// </summary>
    internal void SetStateManagerForTesting(IActorStateManager stateManager)
        => StateManager = stateManager;

    /// <inheritdoc/>
    public async Task<bool> TryClaimAsync(string agentId)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);

        var current = await LoadAsync();

        if (current.AgentId is null)
        {
            await SaveAsync(current with { AgentId = agentId });
            return true;
        }

        // Same agent re-claiming the item is allowed (idempotent restart path).
        return current.AgentId == agentId;
    }

    /// <inheritdoc/>
    public async Task SetPhaseAsync(TaskPhase phase)
    {
        var current = await LoadAsync();
        await SaveAsync(current with { Phase = phase });
    }

    /// <inheritdoc/>
    public async Task SaveBranchAsync(string branchName)
    {
        ArgumentException.ThrowIfNullOrEmpty(branchName);

        var current = await LoadAsync();
        await SaveAsync(current with { BranchName = branchName });
    }

    /// <inheritdoc/>
    public async Task SavePullRequestAsync(int pullRequestNumber)
    {
        if (pullRequestNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pullRequestNumber),
                pullRequestNumber,
                "Pull request number must be positive.");
        }

        var current = await LoadAsync();
        await SaveAsync(current with { PullRequestNumber = pullRequestNumber });
    }

    /// <inheritdoc/>
    public async Task MarkWaitingForReviewAsync()
    {
        var current = await LoadAsync();
        await SaveAsync(current with
        {
            Phase = TaskPhase.AwaitingReview,
            ApprovalStatus = ApprovalStatus.WaitingForReview
        });
    }

    /// <inheritdoc/>
    public async Task MarkApprovedAsync()
    {
        var current = await LoadAsync();
        await SaveAsync(current with { ApprovalStatus = ApprovalStatus.Approved });
    }

    /// <inheritdoc/>
    public async Task MarkChangesRequestedAsync()
    {
        var current = await LoadAsync();
        await SaveAsync(current with
        {
            ApprovalStatus = ApprovalStatus.ChangesRequested,
            Phase = TaskPhase.AgentRunning
        });
    }

    /// <inheritdoc/>
    public async Task<int> RecordRetryAsync()
    {
        var current = await LoadAsync();
        var next = current with { RetryCount = current.RetryCount + 1 };
        await SaveAsync(next);
        return next.RetryCount;
    }

    /// <inheritdoc/>
    public Task<ProgrammingTaskState> GetStateAsync() => LoadAsync();

    private async Task<ProgrammingTaskState> LoadAsync()
    {
        var existing = await StateManager.TryGetStateAsync<ProgrammingTaskState>(StateKey);
        return existing.HasValue
            ? existing.Value
            : ProgrammingTaskState.Empty(Id.GetId());
    }

    private Task SaveAsync(ProgrammingTaskState state)
        => StateManager.SetStateAsync(StateKey, state);
}
