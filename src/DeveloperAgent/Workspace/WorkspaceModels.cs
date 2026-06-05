namespace DeveloperAgent.Workspace;

/// <summary>
/// Aggregate change statistics for a branch relative to its base, derived from
/// <c>git diff --numstat</c>. Used by the scope-limit policy (LLD §P2-H) to enforce
/// <c>MaxChangedFiles</c>/<c>MaxChangedLines</c> before push and the composite
/// <c>MaxPRSize</c> before opening a PR.
/// </summary>
/// <remarks>
/// <see cref="TaskWorkspace"/> and <see cref="CommandResult"/> moved to the
/// <c>Agent.Sandbox</c> library (Step-49); <see cref="DiffStats"/> stays host-side until
/// the git client / workspace manager are extracted to <c>Agent.Workspace</c> (Step-51).
/// </remarks>
/// <param name="ChangedFiles">Number of files that differ from the base.</param>
/// <param name="ChangedLines">Total added + deleted lines. Binary files contribute zero.</param>
public sealed record DiffStats(int ChangedFiles, int ChangedLines);
