namespace ReviewerAgent.Configuration;

/// <summary>Anthropic settings — bound from the <c>Anthropic</c> configuration section.</summary>
public sealed record AnthropicOptions
{
    /// <summary>
    /// Name of the secret holding the Anthropic API key. In Development: user-secrets key.
    /// In production: env-var name (uppercased, hyphens → underscores).
    /// </summary>
    public string ApiKeySecretName { get; init; } = "anthropic-api-key";
}
