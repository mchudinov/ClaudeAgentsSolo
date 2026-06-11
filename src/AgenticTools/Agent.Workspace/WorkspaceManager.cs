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
            ForceDeleteDirectory(dir);
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
            ForceDeleteDirectory(dir);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Recursively deletes <paramref name="dir"/>, first clearing the read-only attribute on every
    /// file. A workspace contains a real git checkout, and git writes loose objects and pack files
    /// under <c>.git/objects</c> as read-only (mode 0444); on Windows
    /// <see cref="Directory.Delete(string, bool)"/> throws
    /// <see cref="UnauthorizedAccessException"/> ("Access to the path … is denied") when it hits one.
    /// Clearing the flag first lets the recursive delete remove the whole tree.
    /// </summary>
    private static void ForceDeleteDirectory(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(dir, recursive: true);
    }
}
