using Microsoft.Extensions.AI;

namespace Agent.Memory;

/// <summary>
/// Production <see cref="IChatHistoryStore"/> backed by the Dapr state store named
/// <see cref="StateStoreName"/> (<c>agent-state-store</c> — the component authored in
/// Step-8 and reused by <see cref="DaprAgentSessionStore"/>). All three operations
/// address the same key: <c>chat-history:{agentId}:{projectItemId}</c>.
/// </summary>
/// <remarks>
/// Mirror of <see cref="DaprAgentSessionStore"/> for the chat-history payload. The
/// agentId is supplied at construction (typically <see cref="Environment.MachineName"/>)
/// to match how <see cref="DaprAgentSessionStore"/> is registered; Load/Delete still take
/// an explicit agentId in the interface so the in-memory store stays a drop-in swap.
/// </remarks>
public sealed class DaprChatHistoryStore : IChatHistoryStore
{
    public const string StateStoreName = "agent-state-store";

    private readonly IDaprStateClient _dapr;
    private readonly string _stateStoreName;
    private readonly string _agentId;

    public DaprChatHistoryStore(IDaprStateClient dapr, string stateStoreName, string agentId)
    {
        ArgumentNullException.ThrowIfNull(dapr);
        ArgumentException.ThrowIfNullOrEmpty(stateStoreName);
        ArgumentException.ThrowIfNullOrEmpty(agentId);

        _dapr = dapr;
        _stateStoreName = stateStoreName;
        _agentId = agentId;
    }

    public async Task<IReadOnlyList<ChatMessage>?> LoadAsync(string agentId, string projectItemId, CancellationToken ct)
    {
        var stored = await _dapr.GetStateAsync<List<ChatMessage>>(
            _stateStoreName, FormatKey(agentId, projectItemId), ct).ConfigureAwait(false);
        return stored;
    }

    public Task SaveAsync(string agentId, string projectItemId, IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messages);
        // Persist as a concrete List<T> so the round-trip type-matches GetStateAsync<List<ChatMessage>>.
        var payload = messages as List<ChatMessage> ?? new List<ChatMessage>(messages);
        return _dapr.SaveStateAsync(_stateStoreName, FormatKey(agentId, projectItemId), payload, ct);
    }

    public Task DeleteAsync(string agentId, string projectItemId, CancellationToken ct) =>
        _dapr.DeleteStateAsync(_stateStoreName, FormatKey(agentId, projectItemId), ct);

    /// <summary>
    /// Canonical key formatter — exposed so callers (and tests) can compute the same key
    /// the store uses without depending on the exact string layout.
    /// </summary>
    public static string FormatKey(string agentId, string projectItemId) =>
        $"chat-history:{agentId}:{projectItemId}";
}
