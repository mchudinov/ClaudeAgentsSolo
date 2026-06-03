namespace ClaudeAgents.GitHub;

/// <summary>
/// Supplies the GitHub token used to authenticate the Octokit transports, decoupling the GitHub
/// layer from how the host resolves secrets. The host registers an implementation that reads the
/// token from its own secret store; the GitHub layer never sees the host's secret bundle.
/// </summary>
public interface IGitHubTokenProvider
{
    /// <summary>Returns the GitHub token (PAT or installation token) for API authentication.</summary>
    string GetToken();
}
