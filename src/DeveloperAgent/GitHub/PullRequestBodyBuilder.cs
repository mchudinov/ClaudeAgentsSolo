namespace DeveloperAgent.GitHub;

/// <summary>
/// Builds the four-section pull request body required by the developer persona (§9).
/// </summary>
public static class PullRequestBodyBuilder
{
    /// <summary>
    /// The canonical four section headers (markdown <c>##</c> form) the developer persona (§9)
    /// requires in every PR body, in their required order. Consumed by the reviewer agent's
    /// missing-section check so the section knowledge is defined exactly once.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredSectionHeaders =
    [
        "## Summary",
        "## User-visible behavior",
        "## Tests/validation run",
        "## Notes/assumptions",
    ];

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
        // Headers come from RequiredSectionHeaders so the canonical set lives in one place.
        return
            $"{RequiredSectionHeaders[0]}\n{summary}\n\n" +
            $"{RequiredSectionHeaders[1]}\n{OrNone(userVisibleBehavior)}\n\n" +
            $"{RequiredSectionHeaders[2]}\n{OrNone(testsValidationRun)}\n\n" +
            $"{RequiredSectionHeaders[3]}\n{OrNone(notesAssumptions)}\n";
    }
}
