namespace DeveloperAgent.Workspace;

/// <summary>
/// Represents a prepared, per-task workspace directory and its associated git state.
/// Returned by <see cref="IWorkspaceManager.PrepareAsync"/> and threaded through the
/// git and sandbox operations for the lifetime of a single task execution.
/// </summary>
public sealed record TaskWorkspace(
    string ProjectItemId,
    string BranchName,
    string RepoRoot,          // absolute path, e.g. /workspace/{itemId}/repo
    string DefaultBranch);    // captured from refs/remotes/origin/HEAD at clone time

/// <summary>
/// The output of a sandboxed child-process invocation.
/// </summary>
public sealed record CommandResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Elapsed,
    bool TimedOut);

/// <summary>
/// Aggregate change statistics for a branch relative to its base, derived from
/// <c>git diff --numstat</c>. Used by the scope-limit policy (LLD §P2-H) to enforce
/// <c>MaxChangedFiles</c>/<c>MaxChangedLines</c> before push and the composite
/// <c>MaxPRSize</c> before opening a PR.
/// </summary>
/// <param name="ChangedFiles">Number of files that differ from the base.</param>
/// <param name="ChangedLines">Total added + deleted lines. Binary files contribute zero.</param>
public sealed record DiffStats(int ChangedFiles, int ChangedLines);
