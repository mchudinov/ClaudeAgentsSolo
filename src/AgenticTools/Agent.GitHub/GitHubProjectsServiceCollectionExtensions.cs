using Microsoft.Extensions.DependencyInjection;

namespace Agent.GitHub;

/// <summary>
/// DI registration for the agent-neutral GitHub Projects client.
/// </summary>
public static class GitHubProjectsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IGitHubProjectsClient"/> and its Octokit transports, plus the two named
    /// HttpClients (<see cref="GitHubHttpClients.Rest"/>, <see cref="GitHubHttpClients.GraphQL"/>).
    /// The host must also register an <see cref="IGitHubTokenProvider"/> and bind
    /// <see cref="GitHubOptions"/>. Egress filtering and resilience are intentionally NOT applied
    /// here — the host supplies them via <paramref name="configureHttpClient"/>, which is invoked on
    /// each named client's builder (e.g. <c>b => b.AddHttpMessageHandler&lt;HostAllowlistHandler&gt;()
    /// .AddStandardResilienceHandler()</c>).
    /// </summary>
    public static IServiceCollection AddGitHubProjectServices(
        this IServiceCollection services,
        Action<IHttpClientBuilder>? configureHttpClient = null)
    {
        services.AddSingleton<IGraphQLTransport, OctokitGraphQLTransport>();
        services.AddSingleton<IRestTransport, OctokitRestTransport>();
        services.AddSingleton<IGitHubProjectsClient, GitHubProjectsClient>();

        var restBuilder = services.AddHttpClient(GitHubHttpClients.Rest);
        var graphQLBuilder = services.AddHttpClient(GitHubHttpClients.GraphQL);
        configureHttpClient?.Invoke(restBuilder);
        configureHttpClient?.Invoke(graphQLBuilder);

        return services;
    }
}
