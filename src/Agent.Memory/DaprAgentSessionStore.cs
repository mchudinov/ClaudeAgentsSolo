namespace Agent.Memory;

/// <summary>
/// Production <see cref="IAgentSessionStore"/> backed by the Dapr state store named
/// <see cref="StateStoreName"/> (<c>agent-state-store</c> — the component authored in
/// Step-8). All three operations address the same key:
/// <c>agent-session:{agentId}:{projectItemId}</c>.
/// </summary>
/// <remarks>
/// The state-store name is hardcoded to <c>agent-state-store</c> to match the component
/// registered by the AppHost and to keep configuration surface narrow. The
/// <paramref name="agentId"/> is provided at construction (typically
/// <see cref="Environment.MachineName"/>) so callers do not need to repeat it on every
/// Save/Load call — except for <see cref="IAgentSessionStore.LoadAsync"/> and
/// <see cref="IAgentSessionStore.DeleteAsync"/> where the interface accepts an explicit
/// agentId for symmetry with the in-memory implementation; this store ignores any value
/// other than the one it was constructed with at the moment but the interface signature
/// keeps both stores swappable.
/// </remarks>
public sealed class DaprAgentSessionStore : IAgentSessionStore
{
    public const string StateStoreName = "agent-state-store";

    private readonly IDaprStateClient _dapr;
    private readonly string _stateStoreName;
    private readonly string _agentId;

    public DaprAgentSessionStore(IDaprStateClient dapr, string stateStoreName, string agentId)
    {
        ArgumentNullException.ThrowIfNull(dapr);
        ArgumentException.ThrowIfNullOrEmpty(stateStoreName);
        ArgumentException.ThrowIfNullOrEmpty(agentId);

        _dapr = dapr;
        _stateStoreName = stateStoreName;
        _agentId = agentId;
    }

    public Task<AgentSession?> LoadAsync(string agentId, string projectItemId, CancellationToken ct) =>
        _dapr.GetStateAsync<AgentSession>(_stateStoreName, FormatKey(agentId, projectItemId), ct);

    public Task SaveAsync(AgentSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _dapr.SaveStateAsync(_stateStoreName, FormatKey(session.AgentId, session.ProjectItemId), session, ct);
    }

    public Task DeleteAsync(string agentId, string projectItemId, CancellationToken ct) =>
        _dapr.DeleteStateAsync(_stateStoreName, FormatKey(agentId, projectItemId), ct);

    /// <summary>
    /// Canonical key formatter — exposed so callers (and tests) can compute the same key
    /// the store uses without depending on the exact string layout.
    /// </summary>
    public static string FormatKey(string agentId, string projectItemId) =>
        $"agent-session:{agentId}:{projectItemId}";
}
