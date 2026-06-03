using DeveloperAgent.GitHub;

namespace DeveloperAgent.Configuration;

/// <summary>
/// Bridges the host's <see cref="SecretsBundle"/> to the GitHub layer's <see cref="IGitHubTokenProvider"/>
/// seam, exposing only the GitHub token (the Anthropic key in the bundle never reaches the GitHub layer).
/// </summary>
public sealed class SecretsBundleGitHubTokenProvider : IGitHubTokenProvider
{
    private readonly SecretsBundle _secrets;

    public SecretsBundleGitHubTokenProvider(SecretsBundle secrets) => _secrets = secrets;

    public string GetToken() => _secrets.GitHubToken;
}
