namespace Agent.Memory;

/// <summary>
/// Persists the two long-lived memory namespaces consumed by
/// <see cref="DaprAgentMemoryContextProvider"/>:
/// <list type="bullet">
/// <item><b>repo memories</b> — durable repository conventions keyed by <c>repoId</c>
///   (Dapr key <c>repo-state:{repoId}</c>). Shared by every task on that repo.</item>
/// <item><b>task memories</b> — lessons scoped to a single GitHub project item keyed by
///   <c>projectItemId</c> (Dapr key <c>task-memory:{projectItemId}</c>).</item>
/// </list>
/// </summary>
/// <remarks>
/// Each memory is a single short fact string (see LLD §Agent Framework memory design,
/// e.g. "Repository uses xUnit"). The production implementation is
/// <see cref="DaprAgentMemoryStore"/>; tests use <see cref="InMemoryAgentMemoryStore"/> to
/// avoid the Dapr sidecar. Like <see cref="IChatHistoryStore"/>, this store round-trips the
/// full list verbatim — the dedup/cap policy lives in
/// <see cref="DaprAgentMemoryContextProvider"/>, not here.
/// </remarks>
public interface IAgentMemoryStore
{
    /// <summary>Returns the repo-convention memories for <paramref name="repoId"/>, or <c>null</c> if none.</summary>
    Task<IReadOnlyList<string>?> LoadRepoMemoriesAsync(string repoId, CancellationToken ct);

    /// <summary>Writes <paramref name="memories"/> for <paramref name="repoId"/>, overwriting any prior list.</summary>
    Task SaveRepoMemoriesAsync(string repoId, IReadOnlyList<string> memories, CancellationToken ct);

    /// <summary>Removes the repo-convention memories for <paramref name="repoId"/>. No-op if absent.</summary>
    Task DeleteRepoMemoriesAsync(string repoId, CancellationToken ct);

    /// <summary>Returns the task-scoped memories for <paramref name="projectItemId"/>, or <c>null</c> if none.</summary>
    Task<IReadOnlyList<string>?> LoadTaskMemoriesAsync(string projectItemId, CancellationToken ct);

    /// <summary>Writes <paramref name="memories"/> for <paramref name="projectItemId"/>, overwriting any prior list.</summary>
    Task SaveTaskMemoriesAsync(string projectItemId, IReadOnlyList<string> memories, CancellationToken ct);

    /// <summary>Removes the task-scoped memories for <paramref name="projectItemId"/>. No-op if absent.</summary>
    Task DeleteTaskMemoriesAsync(string projectItemId, CancellationToken ct);
}
