using System.Collections.Concurrent;

namespace DeveloperAgent.AgentMemory;

/// <summary>
/// Process-local <see cref="IAgentSessionStore"/> backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Used by unit tests that need to exercise the workflow without a running Dapr sidecar.
/// Not registered in production DI — <see cref="DaprAgentSessionStore"/> is the production binding.
/// </summary>
public sealed class InMemoryAgentSessionStore : IAgentSessionStore
{
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new(StringComparer.Ordinal);

    public Task<AgentSession?> LoadAsync(string agentId, string projectItemId, CancellationToken ct)
    {
        _sessions.TryGetValue(Key(agentId, projectItemId), out var session);
        return Task.FromResult<AgentSession?>(session);
    }

    public Task SaveAsync(AgentSession session, CancellationToken ct)
    {
        _sessions[Key(session.AgentId, session.ProjectItemId)] = session;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string agentId, string projectItemId, CancellationToken ct)
    {
        _sessions.TryRemove(Key(agentId, projectItemId), out _);
        return Task.CompletedTask;
    }

    private static string Key(string agentId, string projectItemId) =>
        $"agent-session:{agentId}:{projectItemId}";
}
