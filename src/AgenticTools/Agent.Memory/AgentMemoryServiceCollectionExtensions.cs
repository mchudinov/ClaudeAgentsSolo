using Microsoft.Extensions.DependencyInjection;

namespace Agent.Memory;

/// <summary>
/// DI registration for the agent-neutral, Dapr-backed memory stores.
/// </summary>
public static class AgentMemoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Dapr-backed memory stores as singletons:
    /// <see cref="IDaprStateClient"/> (<see cref="DaprClientStateAdapter"/>),
    /// <see cref="IAgentSessionStore"/> (<see cref="DaprAgentSessionStore"/>),
    /// <see cref="IAgentMemoryStore"/> (<see cref="DaprAgentMemoryStore"/>), and
    /// <see cref="IChatHistoryStore"/> (<see cref="DaprChatHistoryStore"/>).
    /// </summary>
    /// <remarks>
    /// The host must register a <c>Dapr.Client.DaprClient</c> — <see cref="DaprClientStateAdapter"/>
    /// depends on it — exactly as <c>AddGitHubProjectServices</c> requires the host to supply an
    /// <c>IGitHubTokenProvider</c>. The MAF providers (<see cref="DaprChatHistoryProvider"/>,
    /// <see cref="DaprAgentMemoryContextProvider"/>) and the <see cref="ISummarizer"/> /
    /// <see cref="IMemoryExtractor"/> seams are intentionally NOT registered here: they are
    /// constructed per run with runtime-scoped arguments (repoId, projectItemId, window sizes),
    /// not as container singletons.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="agentId">Identity of the agent process owning the sessions/history (typically <c>Environment.MachineName</c>).</param>
    /// <param name="stateStoreName">Dapr state-store component name. Defaults to <see cref="DaprAgentSessionStore.StateStoreName"/> (<c>agent-state-store</c>).</param>
    public static IServiceCollection AddAgentMemoryServices(
        this IServiceCollection services,
        string agentId,
        string stateStoreName = DaprAgentSessionStore.StateStoreName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        ArgumentException.ThrowIfNullOrEmpty(stateStoreName);

        services.AddSingleton<IDaprStateClient, DaprClientStateAdapter>();
        services.AddSingleton<IAgentSessionStore>(sp => new DaprAgentSessionStore(
            sp.GetRequiredService<IDaprStateClient>(), stateStoreName, agentId));
        services.AddSingleton<IAgentMemoryStore>(sp => new DaprAgentMemoryStore(
            sp.GetRequiredService<IDaprStateClient>(), stateStoreName));
        services.AddSingleton<IChatHistoryStore>(sp => new DaprChatHistoryStore(
            sp.GetRequiredService<IDaprStateClient>(), stateStoreName, agentId));

        return services;
    }
}
