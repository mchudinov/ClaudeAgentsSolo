using Agent.Workspace;

namespace DeveloperAgent.Configuration;

/// <summary>
/// Bridges the host's <see cref="SecretsBundle"/> to the workspace layer's <see cref="IGitTokenProvider"/>
/// seam, exposing only the GitHub token used for git-over-HTTPS clone/push auth (the Anthropic key in the
/// bundle never reaches the workspace layer). Parallels <see cref="SecretsBundleGitHubTokenProvider"/> —
/// one tiny adapter per consuming library, so neither agent-neutral library depends on the other.
/// </summary>
public sealed class SecretsBundleGitTokenProvider : IGitTokenProvider
{
    private readonly SecretsBundle _secrets;

    public SecretsBundleGitTokenProvider(SecretsBundle secrets) => _secrets = secrets;

    public string GetToken() => _secrets.GitHubToken;
}
