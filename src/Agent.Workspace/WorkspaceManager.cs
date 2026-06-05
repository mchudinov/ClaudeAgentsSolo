using Agent.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Workspace;

/// <summary>
/// Creates and wipes per-task workspace directories, then orchestrates the initial
/// clone + default-branch resolution via <see cref="IGitClient"/>.
/// </summary>
public sealed class WorkspaceManager : IWorkspaceManager
{
    private readonly IGitClient _git;
    private readonly IOptions<WorkspaceRootOptions> _workspaceOptions;
    private readonly ILogger<WorkspaceManager> _logger;

    /// <summary>Initialises a new <see cref="WorkspaceManager"/>.</summary>
    public WorkspaceManager(
        IGitClient git,
        IOptions<WorkspaceRootOptions> workspaceOptions,
        ILogger<WorkspaceManager> logger)
    {
        _git = git;
        _workspaceOptions = workspaceOptions;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TaskWorkspace> PrepareAsync(
        string projectItemId,
        string branchName,
        string repoUrl,
        CancellationToken ct)
    {
        var rootPath = _workspaceOptions.Value.RootPath;
        var dir = Path.Combine(rootPath, projectItemId);

        // ── 1. Wipe any pre-existing workspace (e.g. crash remnant) ─────────
        if (Directory.Exists(dir))
        {
            _logger.LogInformation("Wiping pre-existing workspace at {Dir}", dir);
            Directory.Delete(dir, recursive: true);
        }

        // ── 2. Create directory structure ────────────────────────────────────
        var repoRoot = Path.Combine(dir, "repo");
        var logsDir = Path.Combine(dir, "logs");
        Directory.CreateDirectory(repoRoot);
        Directory.CreateDirectory(logsDir);
        _logger.LogInformation("Prepared workspace directories at {Dir}", dir);

        // Build a provisional TaskWorkspace (DefaultBranch filled after clone)
        var ws = new TaskWorkspace(
            ProjectItemId: projectItemId,
            BranchName: branchName,
            RepoRoot: repoRoot,
            DefaultBranch: string.Empty);

        // ── 3. Clone ─────────────────────────────────────────────────────────
        await _git.CloneAsync(ws, repoUrl, ct).ConfigureAwait(false);

        // ── 4. Resolve default branch ────────────────────────────────────────
        var defaultBranch = await _git.ResolveDefaultBranchAsync(ws, ct).ConfigureAwait(false);

        return ws with { DefaultBranch = defaultBranch };
    }

    /// <inheritdoc />
    public Task ReleaseAsync(TaskWorkspace workspace, CancellationToken ct)
    {
        var dir = Path.Combine(_workspaceOptions.Value.RootPath, workspace.ProjectItemId);
        if (Directory.Exists(dir))
        {
            _logger.LogInformation("Releasing workspace at {Dir}", dir);
            Directory.Delete(dir, recursive: true);
        }
        return Task.CompletedTask;
    }
}
