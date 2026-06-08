namespace ReviewerAgent.Configuration;

/// <summary>Resolved secrets, fetched once at startup.</summary>
/// <param name="AnthropicApiKey">Anthropic API key.</param>
/// <param name="GitHubToken">GitHub PAT with <c>repo</c> scope.</param>
public sealed record SecretsBundle(string AnthropicApiKey, string GitHubToken);
