namespace DeveloperAgent.Resilience;

/// <summary>
/// Central names for the <see cref="System.Net.Http.HttpClient"/> instances the agent
/// creates via <see cref="System.Net.Http.IHttpClientFactory"/>. Every external HTTP
/// dependency must be reached through one of these named clients so the
/// <c>AddStandardResilienceHandler()</c> policies (timeouts, retries with exponential
/// back-off, circuit breakers, hedging) apply uniformly.
/// </summary>
/// <remarks>
/// <para>
/// The names are paired with the Dapr <c>Resiliency</c> policies in
/// <c>src/DeveloperAgent/dapr-components/resiliency.yaml</c>. The two layers cover
/// different concerns: the Dapr layer governs cross-process service-invocation and
/// state-store calls; the HttpClient layer governs in-process transport to Anthropic
/// and GitHub. The names line up intentionally so an operator tuning one layer can
/// reason about the other.
/// </para>
/// <para>
/// The egress allowlist (Step-23, P2-I part 2/3) attaches a primary
/// <see cref="System.Net.Http.HttpMessageHandler"/> filter to each of these named
/// clients. Do not change the names without updating Step-23.
/// </para>
/// </remarks>
internal static class HttpClientNames
{
    /// <summary>HttpClient used by the Anthropic .NET SDK.</summary>
    public const string Anthropic = "anthropic";

    // The GitHub REST/GraphQL client names now live in Agent.GitHub.GitHubHttpClients,
    // registered by AddGitHubProjectServices; the host composes egress + resilience onto them.
}
