namespace Agent.Review;

/// <summary>
/// Reviewer-engine options — bound by the host from its <c>Reviewer</c> configuration section.
/// Agent-neutral: the engine reads the model id, persona path, the deterministic oversized-diff
/// thresholds, and the set of PR-body section headers it requires.
/// </summary>
public sealed record ReviewerOptions
{
    /// <summary>Anthropic model id the persona scan runs on (e.g. "claude-opus-4-7").</summary>
    public string Model { get; init; } = "claude-opus-4-7";

    /// <summary>Path to the reviewer persona markdown file, relative to <c>ContentRootPath</c>.</summary>
    public string PersonaPath { get; init; } = "personas/reviewer.md";

    /// <summary>Max changed files before the reviewer returns RequestChanges on size alone (no model call).</summary>
    public int MaxDiffFiles { get; init; } = 50;

    /// <summary>Max changed lines (additions + deletions) before RequestChanges on size alone.</summary>
    public int MaxDiffLines { get; init; } = 2_000;

    /// <summary>
    /// PR-body section headers the reviewer requires (each must be present with non-empty content);
    /// a body missing any of them is RequestChanges without a model call. Empty list = skip the
    /// section check entirely. Defaults to [] so the config-binder append-on-default gotcha (Step-41)
    /// cannot double-load it — the canonical list lives solely in the host's appsettings.json.
    /// </summary>
    public IReadOnlyList<string> RequiredPrBodySections { get; init; } = [];
}
