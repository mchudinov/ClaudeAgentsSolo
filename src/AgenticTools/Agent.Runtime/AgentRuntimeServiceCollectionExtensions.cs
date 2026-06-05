using Microsoft.Extensions.DependencyInjection;

namespace Agent.Runtime;

/// <summary>
/// DI registration for the agent-neutral Anthropic chat-client transport.
/// </summary>
public static class AgentRuntimeServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IAgentChatClientFactory"/> (<see cref="AnthropicChatClientFactory"/>) and
    /// the named HttpClient (<see cref="AnthropicHttpClients.ChatClient"/>) the factory sources from.
    /// The host must also register an <see cref="IAnthropicApiKeyProvider"/>. Egress filtering and
    /// resilience are intentionally NOT applied here — the host supplies them via
    /// <paramref name="configureHttpClient"/>, which is invoked on the named client's builder (e.g.
    /// <c>b => b.AddHttpMessageHandler&lt;HostAllowlistHandler&gt;().AddStandardResilienceHandler()</c>),
    /// exactly like <c>Agent.GitHub.AddGitHubProjectServices</c>.
    /// </summary>
    public static IServiceCollection AddAgentRuntimeServices(
        this IServiceCollection services,
        Action<IHttpClientBuilder>? configureHttpClient = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IAgentChatClientFactory, AnthropicChatClientFactory>();

        var builder = services.AddHttpClient(AnthropicHttpClients.ChatClient);
        configureHttpClient?.Invoke(builder);

        return services;
    }
}
