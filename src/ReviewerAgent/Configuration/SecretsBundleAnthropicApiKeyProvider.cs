using Agent.Runtime;

namespace ReviewerAgent.Configuration;

/// <summary>Supplies the Anthropic API key to the runtime layer from the resolved <see cref="SecretsBundle"/>.</summary>
public sealed class SecretsBundleAnthropicApiKeyProvider : IAnthropicApiKeyProvider
{
    private readonly SecretsBundle _secrets;

    public SecretsBundleAnthropicApiKeyProvider(SecretsBundle secrets) => _secrets = secrets;

    public string GetApiKey() => _secrets.AnthropicApiKey;
}
