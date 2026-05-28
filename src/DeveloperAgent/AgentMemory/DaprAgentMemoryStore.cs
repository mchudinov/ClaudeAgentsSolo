namespace DeveloperAgent.AgentMemory;

/// <summary>
/// Production <see cref="IAgentMemoryStore"/> backed by the Dapr state store named
/// <see cref="StateStoreName"/> (<c>agent-state-store</c> — the component authored in
/// Step-8 and reused by <see cref="DaprAgentSessionStore"/> / <see cref="DaprChatHistoryStore"/>).
/// Repo memories live under <c>repo-state:{repoId}</c>; task memories under
/// <c>task-memory:{projectItemId}</c> — the namespaces spelled out verbatim in the LLD.
/// </summary>
/// <remarks>
/// Mirrors <see cref="DaprChatHistoryStore"/>: the <see cref="IDaprStateClient"/> seam keeps
/// the store unit-testable without mocking the non-virtual <c>DaprClient</c> extension surface.
/// </remarks>
public sealed class DaprAgentMemoryStore : IAgentMemoryStore
{
    public const string StateStoreName = "agent-state-store";

    private readonly IDaprStateClient _dapr;
    private readonly string _stateStoreName;

    public DaprAgentMemoryStore(IDaprStateClient dapr, string stateStoreName)
    {
        ArgumentNullException.ThrowIfNull(dapr);
        ArgumentException.ThrowIfNullOrEmpty(stateStoreName);

        _dapr = dapr;
        _stateStoreName = stateStoreName;
    }

    public Task<IReadOnlyList<string>?> LoadRepoMemoriesAsync(string repoId, CancellationToken ct) =>
        LoadAsync(RepoKey(repoId), ct);

    public Task SaveRepoMemoriesAsync(string repoId, IReadOnlyList<string> memories, CancellationToken ct) =>
        SaveAsync(RepoKey(repoId), memories, ct);

    public Task DeleteRepoMemoriesAsync(string repoId, CancellationToken ct) =>
        _dapr.DeleteStateAsync(_stateStoreName, RepoKey(repoId), ct);

    public Task<IReadOnlyList<string>?> LoadTaskMemoriesAsync(string projectItemId, CancellationToken ct) =>
        LoadAsync(TaskKey(projectItemId), ct);

    public Task SaveTaskMemoriesAsync(string projectItemId, IReadOnlyList<string> memories, CancellationToken ct) =>
        SaveAsync(TaskKey(projectItemId), memories, ct);

    public Task DeleteTaskMemoriesAsync(string projectItemId, CancellationToken ct) =>
        _dapr.DeleteStateAsync(_stateStoreName, TaskKey(projectItemId), ct);

    private async Task<IReadOnlyList<string>?> LoadAsync(string key, CancellationToken ct) =>
        await _dapr.GetStateAsync<List<string>>(_stateStoreName, key, ct).ConfigureAwait(false);

    private Task SaveAsync(string key, IReadOnlyList<string> memories, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(memories);
        // Persist as a concrete List<T> so the round-trip type-matches GetStateAsync<List<string>>.
        var payload = memories as List<string> ?? new List<string>(memories);
        return _dapr.SaveStateAsync(_stateStoreName, key, payload, ct);
    }

    /// <summary>Canonical repo-memory key — <c>repo-state:{repoId}</c>.</summary>
    public static string RepoKey(string repoId) => $"repo-state:{repoId}";

    /// <summary>Canonical task-memory key — <c>task-memory:{projectItemId}</c>.</summary>
    public static string TaskKey(string projectItemId) => $"task-memory:{projectItemId}";
}
