namespace Agent.Workspace;

/// <summary>
/// Aggregate change statistics for a branch relative to its base, derived from
/// <c>git diff --numstat</c>. Used by the scope-limit policy (LLD §P2-H) to enforce
/// <c>MaxChangedFiles</c>/<c>MaxChangedLines</c> before push and the composite
/// <c>MaxPRSize</c> before opening a PR.
/// </summary>
/// <param name="ChangedFiles">Number of files that differ from the base.</param>
/// <param name="ChangedLines">Total added + deleted lines. Binary files contribute zero.</param>
public sealed record DiffStats(int ChangedFiles, int ChangedLines);
