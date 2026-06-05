namespace Agent.Workspace;

/// <summary>
/// The diff-scope half of the task scope-limit policy (LLD §P2-H): the changed-file and
/// changed-line caps enforced before a push. Carved out of the host's <c>ScopeLimitOptions</c>
/// in Step-50 so the git/workspace layer depends only on these two diff limits, not on the
/// host's run/PR policy; moved into this library with the git client in Step-51. The host still
/// binds it from the one <c>ScopeLimits</c> configuration section (both halves bind from that one
/// section; these are scalars, so the ConfigurationBinder append-on-default gotcha does not apply).
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
