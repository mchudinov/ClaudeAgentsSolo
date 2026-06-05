namespace Agent.Sandbox;

/// <summary>
/// Represents a prepared, per-task workspace directory and its associated git state.
/// Returned by the host's workspace manager and threaded through the git and sandbox
/// operations for the lifetime of a single task execution.
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
