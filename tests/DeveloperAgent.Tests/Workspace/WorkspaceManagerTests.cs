using DeveloperAgent.Configuration;
using DeveloperAgent.Workspace;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace DeveloperAgent.Tests.Workspace;

/// <summary>
/// Tests for <see cref="WorkspaceManager"/>.
/// All git operations are substituted via <see cref="IGitClient"/> — no real git
/// commands are executed in this test class.
/// </summary>
public sealed class WorkspaceManagerTests : IDisposable
{
    private readonly string _rootPath;
    private readonly IGitClient _git;

    public WorkspaceManagerTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "wm-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_rootPath);
        _git = Substitute.For<IGitClient>();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_rootPath))
                Directory.Delete(_rootPath, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private WorkspaceManager BuildManager(
        string? repoUrl = null,
        string? defaultBranch = null)
    {
        // Configure git client stub
        _git.ResolveDefaultBranchAsync(Arg.Any<TaskWorkspace>(), Arg.Any<CancellationToken>())
            .Returns(defaultBranch ?? "main");

        var workspaceOpts = Options.Create(new WorkspaceOptions { RootPath = _rootPath });
        var githubOpts = Options.Create(new GitHubOptions
        {
            Repository = new RepositoryOptions { Url = repoUrl ?? "https://github.com/test/repo" },
        });
        var logger = Substitute.For<ILogger<WorkspaceManager>>();
        return new WorkspaceManager(_git, workspaceOpts, githubOpts, logger);
    }

    // ── Directory structure ──────────────────────────────────────────────────

    [Fact]
    public async Task PrepareAsync_creates_repo_and_logs_subdirs()
    {
        var manager = BuildManager();
        const string itemId = "item-abc";

        await manager.PrepareAsync(itemId, "agent/test", CancellationToken.None);

        var repoDir = Path.Combine(_rootPath, itemId, "repo");
        var logsDir = Path.Combine(_rootPath, itemId, "logs");

        Directory.Exists(repoDir).Should().BeTrue("repo subdirectory should exist");
        Directory.Exists(logsDir).Should().BeTrue("logs subdirectory should exist");
    }

    [Fact]
    public async Task PrepareAsync_wipes_existing_dir_for_same_itemId()
    {
        var manager = BuildManager();
        const string itemId = "item-wipe";

        // Pre-create a file under the workspace dir
        var existingDir = Path.Combine(_rootPath, itemId, "repo");
        Directory.CreateDirectory(existingDir);
        var existingFile = Path.Combine(existingDir, "stale.txt");
        await File.WriteAllTextAsync(existingFile, "old content");

        await manager.PrepareAsync(itemId, "agent/test", CancellationToken.None);

        // The stale file should be gone
        File.Exists(existingFile).Should().BeFalse("workspace should have been wiped");
    }

    // ── Git client delegation ────────────────────────────────────────────────

    [Fact]
    public async Task PrepareAsync_invokes_GitClient_CloneAsync_with_configured_repo_url()
    {
        const string repoUrl = "https://github.com/myorg/myrepo";
        var manager = BuildManager(repoUrl: repoUrl);

        await manager.PrepareAsync("item-clone", "agent/test", CancellationToken.None);

        await _git.Received(1).CloneAsync(
            Arg.Any<TaskWorkspace>(),
            repoUrl,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrepareAsync_invokes_ResolveDefaultBranchAsync_and_stores_result()
    {
        const string expectedBranch = "main";
        var manager = BuildManager(defaultBranch: expectedBranch);

        var ws = await manager.PrepareAsync("item-branch", "agent/test", CancellationToken.None);

        await _git.Received(1).ResolveDefaultBranchAsync(
            Arg.Any<TaskWorkspace>(),
            Arg.Any<CancellationToken>());

        ws.DefaultBranch.Should().Be(expectedBranch);
    }

    [Fact]
    public async Task PrepareAsync_returns_TaskWorkspace_with_correct_fields()
    {
        var manager = BuildManager(defaultBranch: "main");
        const string itemId = "item-fields";
        const string branchName = "agent/my-branch";

        var ws = await manager.PrepareAsync(itemId, branchName, CancellationToken.None);

        ws.ProjectItemId.Should().Be(itemId);
        ws.BranchName.Should().Be(branchName);
        ws.RepoRoot.Should().Be(Path.Combine(_rootPath, itemId, "repo"));
        ws.DefaultBranch.Should().Be("main");
    }

    // ── ReleaseAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ReleaseAsync_deletes_workspace_dir()
    {
        var manager = BuildManager();
        const string itemId = "item-release";

        var ws = await manager.PrepareAsync(itemId, "agent/test", CancellationToken.None);

        var dir = Path.Combine(_rootPath, itemId);
        Directory.Exists(dir).Should().BeTrue("workspace should exist before release");

        await manager.ReleaseAsync(ws, CancellationToken.None);

        Directory.Exists(dir).Should().BeFalse("workspace should be deleted after release");
    }

    [Fact]
    public async Task ReleaseAsync_is_idempotent_for_already_deleted_dir()
    {
        var manager = BuildManager();
        var ws = new TaskWorkspace("nonexistent", "agent/test", "/some/path", "main");

        // Should not throw even if the directory doesn't exist
        var act = async () => await manager.ReleaseAsync(ws, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
