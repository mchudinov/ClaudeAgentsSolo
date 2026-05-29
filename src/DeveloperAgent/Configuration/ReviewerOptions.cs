namespace DeveloperAgent.Configuration;

/// <summary>
/// Reviewer-agent runtime options — bound from the <c>Reviewer</c> configuration section.
/// Controls the reviewer persona path and the deterministic oversized-diff thresholds the
/// reviewer enforces before it ever invokes the model.
/// </summary>
public sealed record ReviewerOptions
{
    /// <summary>
    /// Path to the reviewer persona markdown file, relative to <c>ContentRootPath</c>.
    /// Loaded into the reviewer's system prompt for the LLM-backed persona-violation scan.
    /// </summary>
    public string PersonaPath { get; init; } = "personas/reviewer.md";

    /// <summary>
    /// Maximum number of changed files a PR may contain before the reviewer returns
    /// <c>RequestChanges</c> on size alone (deterministic, no model call). A reviewer-side
    /// limit distinct from the developer-side <c>ScopeLimits.MaxPRChangedFiles</c> gate.
    /// </summary>
    public int MaxDiffFiles { get; init; } = 50;

    /// <summary>
    /// Maximum number of changed lines (added + deleted across all files) a PR may contain
    /// before the reviewer returns <c>RequestChanges</c> on size alone (deterministic).
    /// </summary>
    public int MaxDiffLines { get; init; } = 2_000;
}
