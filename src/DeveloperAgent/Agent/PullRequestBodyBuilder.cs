using ClaudeAgents.GitHub;

namespace DeveloperAgent.Agent;

/// <summary>
/// Builds the four-section pull request body required by the developer persona (§9). This is
/// developer-agent policy — the canonical headers, their order, and the "Summary must not be
/// empty / optional fields render as None" rules — layered over the agent-neutral
/// <see cref="MarkdownSectionBuilder"/> rendering mechanics.
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

        // The four headers + per-field defaulting are the policy; MarkdownSectionBuilder owns the
        // byte-identical rendering (explicit \n, blank line between sections).
        return MarkdownSectionBuilder.Build(
            RequiredSectionHeaders,
            [summary, OrNone(userVisibleBehavior), OrNone(testsValidationRun), OrNone(notesAssumptions)]);
    }
}
