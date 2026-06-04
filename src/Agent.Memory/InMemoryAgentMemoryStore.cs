using System.Collections.Concurrent;

namespace Agent.Memory;

/// <summary>
/// Process-local <see cref="IAgentMemoryStore"/> backed by <see cref="ConcurrentDictionary{TKey,TValue}"/>s.
/// Used by unit tests exercising <see cref="DaprAgentMemoryContextProvider"/> without a Dapr sidecar.
/// Not registered in production DI — <see cref="DaprAgentMemoryStore"/> is the production binding.
/// </summary>
public sealed class InMemoryAgentMemoryStore : IAgentMemoryStore
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _repo = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _task = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<string>?> LoadRepoMemoriesAsync(string repoId, CancellationToken ct) =>
        Load(_repo, repoId);

    public Task SaveRepoMemoriesAsync(string repoId, IReadOnlyList<string> memories, CancellationToken ct) =>
        Save(_repo, repoId, memories);

    public Task DeleteRepoMemoriesAsync(string repoId, CancellationToken ct)
    {
        _repo.TryRemove(repoId, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>?> LoadTaskMemoriesAsync(string projectItemId, CancellationToken ct) =>
        Load(_task, projectItemId);

    public Task SaveTaskMemoriesAsync(string projectItemId, IReadOnlyList<string> memories, CancellationToken ct) =>
        Save(_task, projectItemId, memories);

    public Task DeleteTaskMemoriesAsync(string projectItemId, CancellationToken ct)
    {
        _task.TryRemove(projectItemId, out _);
        return Task.CompletedTask;
    }

    private static Task<IReadOnlyList<string>?> Load(
        ConcurrentDictionary<string, IReadOnlyList<string>> bag, string id)
    {
        bag.TryGetValue(id, out var memories);
        return Task.FromResult(memories);
    }

    private static Task Save(
        ConcurrentDictionary<string, IReadOnlyList<string>> bag, string id, IReadOnlyList<string> memories)
    {
        ArgumentNullException.ThrowIfNull(memories);
        // Defensive copy so later mutations to the caller's list cannot retroactively
        // alter what tests believe was persisted.
        bag[id] = new List<string>(memories);
        return Task.CompletedTask;
    }
}
