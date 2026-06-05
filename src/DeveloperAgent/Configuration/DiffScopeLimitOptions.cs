namespace DeveloperAgent.Configuration;

/// <summary>
/// The diff-scope half of the task scope-limit policy (LLD §P2-H): the changed-file and
/// changed-line caps enforced before a push. Carved out of <c>ScopeLimitOptions</c>
/// in Step-50 so the git/workspace layer depends only on these two diff limits, not on the
/// host's run/PR policy. Still bound from the same <c>ScopeLimits</c> configuration section
/// (the host binds both halves from the one section; these are scalars, so no double-append).
/// </summary>
public sealed record DiffScopeLimitOptions
{
    /// <summary>
    /// Maximum number of changed files (relative to the default branch) permitted
    /// before a push. Checked via <c>git diff --numstat</c> in
    /// <c>GitClient.PushAsync</c> prior to pushing.
    /// </summary>
    public int MaxChangedFiles { get; init; } = 50;

    /// <summary>
    /// Maximum number of changed lines (added + deleted, relative to the default
    /// branch) permitted before a push. Checked via <c>git diff --numstat</c>.
    /// Binary files contribute zero lines (numstat emits <c>-</c> for them).
    /// </summary>
    public int MaxChangedLines { get; init; } = 2_000;
}
