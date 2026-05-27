using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace DeveloperAgent.AgentMemory;

/// <summary>
/// Process-local <see cref="IChatHistoryStore"/> backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Used by unit tests exercising <see cref="DaprChatHistoryProvider"/> without a Dapr sidecar.
/// Not registered in production DI — <see cref="DaprChatHistoryStore"/> is the production binding.
/// </summary>
public sealed class InMemoryChatHistoryStore : IChatHistoryStore
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<ChatMessage>> _histories =
        new(StringComparer.Ordinal);

    public Task<IReadOnlyList<ChatMessage>?> LoadAsync(string agentId, string projectItemId, CancellationToken ct)
    {
        _histories.TryGetValue(Key(agentId, projectItemId), out var messages);
        return Task.FromResult(messages);
    }

    public Task SaveAsync(string agentId, string projectItemId, IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messages);
        // Defensive copy so later mutations to the caller's list cannot retroactively
        // alter what tests believe was persisted.
        _histories[Key(agentId, projectItemId)] = new List<ChatMessage>(messages);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string agentId, string projectItemId, CancellationToken ct)
    {
        _histories.TryRemove(Key(agentId, projectItemId), out _);
        return Task.CompletedTask;
    }

    private static string Key(string agentId, string projectItemId) =>
        $"chat-history:{agentId}:{projectItemId}";
}
