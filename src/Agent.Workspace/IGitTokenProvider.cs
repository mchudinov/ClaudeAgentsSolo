namespace Agent.Workspace;

/// <summary>
/// Supplies the GitHub token used to authenticate <c>git clone</c>/<c>git push</c> over HTTPS,
/// decoupling the workspace/git layer from how the host resolves secrets. The host registers an
/// implementation that reads the token from its own secret store; the workspace layer never sees
/// the host's secret bundle.
/// </summary>
/// <remarks>
/// This mirrors the <c>Agent.GitHub.IGitHubTokenProvider</c> seam but is owned here so the
/// workspace library carries no <c>ProjectReference</c> on <c>Agent.GitHub</c> (which would drag
/// Octokit in for nothing — the git CLI needs only the raw token string). It follows the same
/// host-supplied-seam pattern as <c>Agent.Runtime.IAnthropicApiKeyProvider</c>: each agent-neutral
/// library owns its own secret seam and the host implements all of them over its secret bundle.
/// </remarks>
public interface IGitTokenProvider
{
    /// <summary>
    /// Returns the GitHub token (PAT or installation token) used as the password under the
    /// <c>x-access-token</c> username in git-over-HTTPS Basic auth. May be empty when no auth
    /// is required (e.g. local file-system remotes in tests), in which case no auth header is added.
    /// </summary>
    string GetToken();
}
