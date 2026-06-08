using Agent.GitHub;

namespace ReviewerAgent.Configuration;

/// <summary>Supplies the GitHub token to the GitHub layer from the resolved <see cref="SecretsBundle"/>.</summary>
public sealed class SecretsBundleGitHubTokenProvider : IGitHubTokenProvider
{
    private readonly SecretsBundle _secrets;

    public SecretsBundleGitHubTokenProvider(SecretsBundle secrets) => _secrets = secrets;

    public string GetToken() => _secrets.GitHubToken;
}
