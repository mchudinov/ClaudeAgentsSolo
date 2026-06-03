namespace ClaudeAgents.GitHub;

/// <summary>
/// Thrown when a GitHub call is attempted but required configuration
/// (<c>GitHub:Owner</c> and/or <c>GitHub:Project:Number</c>) is missing.
/// Lifecycle code catches this to log a quiet, actionable message instead
/// of a JSON-parsing stack trace from the empty-response path.
/// </summary>
public sealed class GitHubNotConfiguredException : InvalidOperationException
{
    public GitHubNotConfiguredException(string message) : base(message) { }
}
