namespace DeveloperAgent.Triage;

/// <summary>
/// Builds the markdown comment posted to a GitHub item when the relevance-triage gate rejects it and
/// parks it in the Backlog column.
/// </summary>
/// <remarks>
/// Because Backlog is a write-only holding state (the agent never re-picks a parked item), this
/// comment is the human recovery path: it states that triage auto-parked the item, includes the
/// model's reason, and tells a maintainer how to put the item back into play. Keep it explicit.
/// </remarks>
public static class TriageCommentFormatter
{
    /// <summary>Formats the rejection comment from the triage verdict's <paramref name="reason"/>.</summary>
    public static string Format(string reason)
    {
        var explanation = string.IsNullOrWhiteSpace(reason)
            ? "the item was judged out of scope for this project or outside the agent's skill"
            : reason.Trim();

        return
            "**Auto-triaged to Backlog by the relevance-triage gate.**\n\n" +
            $"Reason: {explanation}\n\n" +
            "The agent did not start work on this item. If this is wrong, move the item back to " +
            "**Ready** to re-queue it (and consider refining the `Triage:RepoScope` configuration).";
    }
}
