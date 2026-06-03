namespace Agent.Memory;

/// <summary>
/// Persists <see cref="AgentSession"/> records by (agent, project item) pair.
/// Production implementation is <see cref="DaprAgentSessionStore"/>; tests use
/// <see cref="InMemoryAgentSessionStore"/> to avoid the Dapr sidecar dependency.
/// </summary>
public interface IAgentSessionStore
{
    /// <summary>
    /// Returns the persisted session for <paramref name="agentId"/>/<paramref name="projectItemId"/>,
    /// or <c>null</c> if none exists yet.
    /// </summary>
    Task<AgentSession?> LoadAsync(string agentId, string projectItemId, CancellationToken ct);

    /// <summary>
    /// Writes <paramref name="session"/>, overwriting any prior record for the same key.
    /// </summary>
    Task SaveAsync(AgentSession session, CancellationToken ct);

    /// <summary>
    /// Removes the persisted session for <paramref name="agentId"/>/<paramref name="projectItemId"/>.
    /// No-op if no record exists.
    /// </summary>
    Task DeleteAsync(string agentId, string projectItemId, CancellationToken ct);
}
