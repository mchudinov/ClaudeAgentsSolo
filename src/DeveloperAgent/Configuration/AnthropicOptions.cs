namespace DeveloperAgent.Configuration;

/// <summary>Anthropic API configuration. The actual key is resolved via <see cref="ISecretResolver"/>.</summary>
public sealed record AnthropicOptions
{
    /// <summary>
    /// Name of the secret holding the Anthropic API key.
    /// In Development: user-secrets key. In production: env-var name (uppercased, hyphens → underscores).
    /// </summary>
    public string ApiKeySecretName { get; init; } = "anthropic-api-key";
}
