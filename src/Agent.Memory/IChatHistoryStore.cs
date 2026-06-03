using Microsoft.Extensions.AI;

namespace Agent.Memory;

/// <summary>
/// Persists the windowed/compacted <see cref="ChatMessage"/> list backing a single
/// <see cref="DaprChatHistoryProvider"/> instance, keyed by (agentId, projectItemId).
/// </summary>
/// <remarks>
/// Production implementation is <see cref="DaprChatHistoryStore"/>; tests use
/// <see cref="InMemoryChatHistoryStore"/> to avoid the Dapr sidecar dependency. The store
/// always round-trips the *full* persisted list — the windowing/compaction policy lives in
/// <see cref="DaprChatHistoryProvider.StoreChatHistoryAsync"/>, not here.
/// </remarks>
public interface IChatHistoryStore
{
    /// <summary>
    /// Returns the persisted chat-history list for <paramref name="agentId"/>/<paramref name="projectItemId"/>,
    /// or <c>null</c> if none exists yet.
    /// </summary>
    Task<IReadOnlyList<ChatMessage>?> LoadAsync(string agentId, string projectItemId, CancellationToken ct);

    /// <summary>
    /// Writes <paramref name="messages"/>, overwriting any prior list for the same key.
    /// </summary>
    Task SaveAsync(string agentId, string projectItemId, IReadOnlyList<ChatMessage> messages, CancellationToken ct);

    /// <summary>
    /// Removes the persisted list for <paramref name="agentId"/>/<paramref name="projectItemId"/>.
    /// No-op if no record exists.
    /// </summary>
    Task DeleteAsync(string agentId, string projectItemId, CancellationToken ct);
}
