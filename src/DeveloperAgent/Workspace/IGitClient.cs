namespace DeveloperAgent.Workspace;

/// <summary>
/// Provides git operations needed by the lifecycle loop and the agent tools.
/// All methods shell out to the <c>git</c> CLI via <see cref="ICommandSandbox"/>.
/// </summary>
public interface IGitClient
{
    /// <summary>
    /// Clones <paramref name="repoUrl"/> into <c>{ws.RepoRoot}</c>.
    /// For HTTPS URLs the GitHub token is passed via an <c>http.extraheader</c>
    /// config flag — it is never written to disk.
    /// </summary>
    Task CloneAsync(TaskWorkspace ws, string repoUrl, CancellationToken ct);

    /// <summary>
    /// Runs <c>git checkout -b {ws.BranchName}</c> in the workspace repo root.
    /// Fails if a local branch with that name already exists.
    /// </summary>
    Task CheckoutNewBranchAsync(TaskWorkspace ws, CancellationToken ct);

    /// <summary>
    /// Reads <c>refs/remotes/origin/HEAD</c> and returns the short branch name
    /// (e.g. <c>"main"</c>).
    /// </summary>
    Task<string> ResolveDefaultBranchAsync(TaskWorkspace ws, CancellationToken ct);

    /// <summary>
    /// Stages the given path specs (<c>git add</c>).
    /// </summary>
    Task AddAsync(TaskWorkspace ws, IReadOnlyList<string> pathspecs, CancellationToken ct);

    /// <summary>
    /// Creates a commit with the given subject and body.
    /// </summary>
    Task CommitAsync(TaskWorkspace ws, string subject, string body, CancellationToken ct);

    /// <summary>
    /// Pushes the current branch to origin.
    /// Uses <c>--set-upstream</c> so the remote-tracking ref is established.
    /// Refuses to push if HEAD is currently on <c>ws.DefaultBranch</c>.
    /// </summary>
    Task PushAsync(TaskWorkspace ws, CancellationToken ct);

    /// <summary>
    /// Returns <c>git status --porcelain</c> output.
    /// </summary>
    Task<string> StatusAsync(TaskWorkspace ws, CancellationToken ct);

    /// <summary>
    /// Returns <c>git diff {base}...HEAD</c> output for use in PR-body generation.
    /// </summary>
    Task<string> DiffAsync(TaskWorkspace ws, string @base, CancellationToken ct);
}
