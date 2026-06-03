namespace ClaudeAgents.GitHub;

/// <summary>
/// Names of the <see cref="System.Net.Http.IHttpClientFactory"/>-managed clients the GitHub
/// transports use. <see cref="AddGitHubProjectServices"/> registers these; the host composes its
/// egress filter and resilience pipeline onto them via the <c>configureHttpClient</c> callback.
/// The string values are a contract shared with the host's egress allowlist and any Dapr resiliency
/// policy keyed by client name.
/// </summary>
public static class GitHubHttpClients
{
    /// <summary>HttpClient used by Octokit (GitHub REST).</summary>
    public const string Rest = "github-rest";

    /// <summary>HttpClient used by Octokit.GraphQL (GitHub GraphQL).</summary>
    public const string GraphQL = "github-graphql";
}
