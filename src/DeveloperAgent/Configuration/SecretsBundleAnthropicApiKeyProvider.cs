using Agent.Runtime;

namespace DeveloperAgent.Configuration;

/// <summary>
/// Bridges the host's <see cref="SecretsBundle"/> to the runtime layer's
/// <see cref="IAnthropicApiKeyProvider"/> seam, exposing only the Anthropic key (the GitHub token in
/// the bundle never reaches the runtime layer). Mirrors <see cref="SecretsBundleGitHubTokenProvider"/>.
/// </summary>
public sealed class SecretsBundleAnthropicApiKeyProvider : IAnthropicApiKeyProvider
{
    private readonly SecretsBundle _secrets;

    public SecretsBundleAnthropicApiKeyProvider(SecretsBundle secrets) => _secrets = secrets;

    public string GetApiKey() => _secrets.AnthropicApiKey;
}
