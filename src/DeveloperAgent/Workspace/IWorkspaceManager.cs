namespace DeveloperAgent.Workspace;

/// <summary>
/// Creates and destroys per-task workspace directories.
/// Each task receives an isolated directory under
/// <see cref="global::Agent.Sandbox.WorkspaceOptions.RootPath"/>.
/// </summary>
public interface IWorkspaceManager
{
    /// <summary>
    /// Prepares a fresh workspace for the given project item.
    /// <list type="number">
    ///   <item>Wipes any pre-existing directory for <paramref name="projectItemId"/>.</item>
    ///   <item>Creates <c>{RootPath}/{projectItemId}/repo</c> and <c>.../logs</c>.</item>
    ///   <item>Clones the configured repository.</item>
    ///   <item>Resolves the default branch from <c>refs/remotes/origin/HEAD</c>.</item>
    /// </list>
    /// </summary>
    /// <param name="projectItemId">Unique identifier of the GitHub Project item.</param>
    /// <param name="branchName">
    /// Agent branch name (as produced by <see cref="BranchName.ForTask"/>).
    /// Stored on the returned <see cref="TaskWorkspace"/> so callers can run
    /// <see cref="IGitClient.CheckoutNewBranchAsync"/> after this method returns.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A fully initialised <see cref="TaskWorkspace"/>.</returns>
    Task<TaskWorkspace> PrepareAsync(string projectItemId, string branchName, CancellationToken ct);

    /// <summary>
    /// Wipes the workspace directory for the given workspace.
    /// Called on both success and terminal failure.
    /// </summary>
    Task ReleaseAsync(TaskWorkspace workspace, CancellationToken ct);
}
