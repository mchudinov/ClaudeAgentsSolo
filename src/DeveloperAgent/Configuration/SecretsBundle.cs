namespace DeveloperAgent.Configuration;

/// <summary>
/// Holds both secrets resolved eagerly at startup.
/// Registered as a singleton so downstream services do not need to inject <see cref="Library.Secrets.ISecretResolver"/> directly.
/// </summary>
/// <param name="AnthropicApiKey">Anthropic API key.</param>
/// <param name="GitHubToken">GitHub PAT with <c>repo</c> and <c>project</c> scopes.</param>
public sealed record SecretsBundle(string AnthropicApiKey, string GitHubToken);
