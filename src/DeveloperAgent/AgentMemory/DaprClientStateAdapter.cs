using Dapr.Client;

namespace DeveloperAgent.AgentMemory;

/// <summary>
/// Production <see cref="IDaprStateClient"/> implementation. Thin pass-through to
/// <see cref="DaprClient.SaveStateAsync{TValue}(string, string, TValue, Dapr.Client.StateOptions, System.Collections.Generic.IReadOnlyDictionary{string, string}, CancellationToken)"/>,
/// <see cref="DaprClient.GetStateAsync{TValue}(string, string, ConsistencyMode?, System.Collections.Generic.IReadOnlyDictionary{string, string}, CancellationToken)"/>,
/// and <see cref="DaprClient.DeleteStateAsync"/>.
/// </summary>
public sealed class DaprClientStateAdapter : IDaprStateClient
{
    private readonly DaprClient _client;

    public DaprClientStateAdapter(DaprClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public Task<T?> GetStateAsync<T>(string storeName, string key, CancellationToken ct) =>
        _client.GetStateAsync<T?>(storeName, key, cancellationToken: ct);

    public Task SaveStateAsync<T>(string storeName, string key, T value, CancellationToken ct) =>
        _client.SaveStateAsync(storeName, key, value, cancellationToken: ct);

    public Task DeleteStateAsync(string storeName, string key, CancellationToken ct) =>
        _client.DeleteStateAsync(storeName, key, cancellationToken: ct);
}
