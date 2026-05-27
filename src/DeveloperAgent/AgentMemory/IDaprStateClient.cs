namespace DeveloperAgent.AgentMemory;

/// <summary>
/// Thin seam over the three Dapr state-store operations that
/// <see cref="DaprAgentSessionStore"/> uses. Wrapping these calls keeps
/// <see cref="DaprAgentSessionStore"/> unit-testable without having to mock the
/// non-virtual extension surface on <c>Dapr.Client.DaprClient</c>.
/// </summary>
/// <remarks>
/// The production implementation is <see cref="DaprClientStateAdapter"/>, a trivial
/// pass-through to <c>DaprClient.SaveStateAsync</c>/<c>GetStateAsync</c>/<c>DeleteStateAsync</c>.
/// Tests can substitute this interface to assert on the exact store name and key
/// the store builds.
/// </remarks>
public interface IDaprStateClient
{
    Task<T?> GetStateAsync<T>(string storeName, string key, CancellationToken ct);
    Task SaveStateAsync<T>(string storeName, string key, T value, CancellationToken ct);
    Task DeleteStateAsync(string storeName, string key, CancellationToken ct);
}
