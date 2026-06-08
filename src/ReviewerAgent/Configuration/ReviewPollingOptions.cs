namespace ReviewerAgent.Configuration;

/// <summary>
/// Host polling policy — bound from the <c>Reviewer</c> configuration section (shares the section
/// with the engine's <c>ReviewerOptions</c>; each record ignores the other's keys).
/// </summary>
public sealed record ReviewPollingOptions
{
    /// <summary>Seconds between open-PR sweeps.</summary>
    public int PollIntervalSeconds { get; init; } = 60;

    /// <summary>GitHub login whose prior reviews drive idempotency (the bot account the token authenticates as).</summary>
    public string ReviewerLogin { get; init; } = "";

    /// <summary>When true, draft PRs are skipped.</summary>
    public bool SkipDrafts { get; init; } = true;

    /// <summary>
    /// When non-empty, only PRs whose author login is in this list are reviewed. Empty = review all.
    /// Defaults to [] so the config-binder append-on-default gotcha cannot double-load it.
    /// </summary>
    public IReadOnlyList<string> AuthorAllowList { get; init; } = [];
}
