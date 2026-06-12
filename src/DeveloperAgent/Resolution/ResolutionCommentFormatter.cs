namespace DeveloperAgent.Resolution;

/// <summary>
/// Builds the markdown comment posted to a GitHub item when the already-resolved gate judges the work
/// already implemented in the real code and parks the item in the Backlog column.
/// </summary>
/// <remarks>
/// Kept deliberately distinct from <see cref="DeveloperAgent.Triage.TriageCommentFormatter"/> (out of
/// scope) and the failure/closed-PR park comments. Because Backlog is write-only, the comment is the
/// human recovery path — and it steers toward marking the item <b>Done</b> (if the implementation is
/// confirmed) rather than back to <b>Ready</b>, which would only re-trigger this same check.
/// </remarks>
public static class ResolutionCommentFormatter
{
    /// <summary>Formats the already-resolved park comment from the verdict's <paramref name="reason"/>.</summary>
    public static string Format(string reason)
    {
        var explanation = string.IsNullOrWhiteSpace(reason)
            ? "the work this item describes already appears to be implemented in the repository"
            : reason.Trim();

        return
            "**Returned to Backlog: this item appears to already be implemented.**\n\n" +
            $"Reason: {explanation}\n\n" +
            "The agent did not start work on it. If the implementation is confirmed, mark the item " +
            "**Done**. If it is in fact incomplete, move it back to **Ready** to re-queue it (note that " +
            "re-queuing will run this already-implemented check again).";
    }
}
