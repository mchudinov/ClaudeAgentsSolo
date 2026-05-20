namespace DeveloperAgent.GitHub;

/// <summary>
/// Builds the four-section pull request body required by the developer persona (§9).
/// </summary>
public static class PullRequestBodyBuilder
{
    /// <summary>
    /// Builds the canonical four-section PR body.
    /// </summary>
    /// <param name="summary">What the PR changes and why. Must not be empty.</param>
    /// <param name="userVisibleBehavior">What an external caller now sees. Use "No user-visible behavior change" for pure refactors.</param>
    /// <param name="testsValidationRun">Which test groups ran and their results. Empty renders as "None".</param>
    /// <param name="notesAssumptions">Anything the reviewer needs to know. Empty renders as "None".</param>
    /// <returns>Markdown string with exactly four sections in the required order.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="summary"/> is null or empty.</exception>
    public static string Build(
        string summary,
        string userVisibleBehavior,
        string testsValidationRun,
        string notesAssumptions)
    {
        if (string.IsNullOrWhiteSpace(summary))
            throw new ArgumentException("PR body Summary cannot be empty", nameof(summary));

        static string OrNone(string value) =>
            string.IsNullOrWhiteSpace(value) ? "None" : value;

        // Explicit \n so the body is byte-identical regardless of the source file's line endings.
        return
            $"## Summary\n{summary}\n\n" +
            $"## User-visible behavior\n{OrNone(userVisibleBehavior)}\n\n" +
            $"## Tests/validation run\n{OrNone(testsValidationRun)}\n\n" +
            $"## Notes/assumptions\n{OrNone(notesAssumptions)}\n";
    }
}
