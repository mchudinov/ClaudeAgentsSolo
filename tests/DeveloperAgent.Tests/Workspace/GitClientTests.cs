using DeveloperAgent.Configuration;
using DeveloperAgent.Tests.Sandbox;
using DeveloperAgent.Workspace;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace DeveloperAgent.Tests.Workspace;

/// <summary>
/// Tests for <see cref="GitClient"/> that exercise real git commands against a temporary
/// local repository. Each test gets its own fresh clone so state changes don't bleed between tests.
/// </summary>
public sealed class GitClientTests : IClassFixture<TempRepoFixture>
{
    private readonly TempRepoFixture _fixture;

    public GitClientTests(TempRepoFixture fixture)
    {
        _fixture = fixture;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a fresh <see cref="GitClient"/> backed by a real <see cref="CommandSandbox"/>
    /// that uses the default workspace root = the fixture's temp dir.
    /// </summary>
    private GitClient BuildClient(string workspaceRoot, ScopeLimitOptions? scopeLimits = null)
    {
        var workspaceOpts = Options.Create(new WorkspaceOptions
        {
            RootPath = workspaceRoot,
            AllowedCommands = ProductionSandboxConfig.AllowedCommands,
        });
        var githubOpts = Options.Create(new GitHubOptions());
        var scopeOpts = Options.Create(scopeLimits ?? new ScopeLimitOptions());
        var secrets = new SecretsBundle("", "");
        var sandboxOpts = Options.Create(new SandboxOptions
        {
            DenyPathPatterns = [],
            SecretFileRegexes = [],
            DeniedCommands = [],
        });

        // CommandSandbox / DefaultProcessRunner have internal ctors in the Agent.Sandbox
        // library (Step-49), so build the sandbox through the public AddSandboxServices DI path
        // rather than constructing it directly.
        var sandbox = ResolveSandbox(workspaceOpts, sandboxOpts);

        return new GitClient(
            sandbox,
            workspaceOpts,
            githubOpts,
            scopeOpts,
            secrets,
            Substitute.For<ILogger<GitClient>>());
    }

    /// <summary>
    /// Builds a real <see cref="ICommandSandbox"/> (backed by the real <c>DefaultProcessRunner</c>)
    /// via the library's public <c>AddSandboxServices</c> registration.
    /// </summary>
    private static ICommandSandbox ResolveSandbox(
        IOptions<WorkspaceOptions> workspaceOpts,
        IOptions<SandboxOptions> sandboxOpts)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<WorkspaceOptions>>(workspaceOpts);
        services.AddSingleton<IOptions<SandboxOptions>>(sandboxOpts);
        services.AddSingleton<IOptions<ContainerRuntimeOptions>>(Options.Create(new ContainerRuntimeOptions()));
        services.AddSandboxServices();
        return services.BuildServiceProvider().GetRequiredService<ICommandSandbox>();
    }

    /// <summary>Creates a fresh clone of the fixture's bare upstream.</summary>
    private (string itemDir, string repoRoot, TaskWorkspace ws) CreateFreshClone()
    {
        var itemId = Guid.NewGuid().ToString("N")[..8];
        var itemDir = Path.Combine(_fixture.TempDir, itemId);
        var repoRoot = Path.Combine(itemDir, "repo");
        Directory.CreateDirectory(repoRoot);

        // Clone the bare upstream using plain git (bypasses sandbox, which is tested separately).
        RunGit(_fixture.TempDir, "clone", _fixture.UpstreamUrl, repoRoot);

        // Set refs/remotes/origin/HEAD so ResolveDefaultBranchAsync works.
        // git clone from a local bare repo may not auto-set this; set it explicitly.
        RunGit(repoRoot, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");

        // Set local git identity so commits don't fail in environments with no global git config.
        // Disable GPG signing at the repo level for tests (mirrors what GitClient.CommitAsync does
        // via -c, but also covers any direct RunGit calls in test setup).
        RunGit(repoRoot, "config", "user.email", "test@example.com");
        RunGit(repoRoot, "config", "user.name", "Test");
        RunGit(repoRoot, "config", "commit.gpgsign", "false");

        var ws = new TaskWorkspace(
            ProjectItemId: itemId,
            BranchName: "agent/test-branch",
            RepoRoot: repoRoot,
            DefaultBranch: "main");

        return (itemDir, repoRoot, ws);
    }

    private static void RunGit(string cwd, params string[] args)
    {
        using var p = new System.Diagnostics.Process();
        p.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) p.StartInfo.ArgumentList.Add(a);
        p.Start();
        p.WaitForExit();
    }

    private static string RunGitOutput(string cwd, params string[] args)
    {
        using var p = new System.Diagnostics.Process();
        p.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) p.StartInfo.ArgumentList.Add(a);
        p.Start();
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return output;
    }

    // ── CloneAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CloneAsync_creates_repo_root_and_origin_remote()
    {
        var itemId = Guid.NewGuid().ToString("N")[..8];
        var itemDir = Path.Combine(_fixture.TempDir, itemId);
        Directory.CreateDirectory(Path.Combine(itemDir, "repo"));

        var ws = new TaskWorkspace(
            ProjectItemId: itemId,
            BranchName: "agent/test",
            RepoRoot: Path.Combine(itemDir, "repo"),
            DefaultBranch: "main");

        var client = BuildClient(_fixture.TempDir);

        await client.CloneAsync(ws, _fixture.UpstreamUrl, CancellationToken.None);

        // .git directory should exist
        Directory.Exists(Path.Combine(ws.RepoRoot, ".git")).Should().BeTrue();

        // The config should reference the upstream URL as an origin remote.
        // On Windows, git may escape backslashes in stored paths, so check for
        // the presence of "origin" remote config section rather than the exact URL string.
        var configContent = File.ReadAllText(Path.Combine(ws.RepoRoot, ".git", "config"));
        configContent.Should().Contain("[remote \"origin\"]",
            because: "a cloned repo should have an origin remote configured");
    }

    // ── CheckoutNewBranchAsync ────────────────────────────────────────────────

    [Fact]
    public async Task CheckoutNewBranchAsync_puts_HEAD_on_new_branch()
    {
        var (_, repoRoot, ws) = CreateFreshClone();
        var client = BuildClient(_fixture.TempDir);

        await client.CheckoutNewBranchAsync(ws, CancellationToken.None);

        var head = RunGitOutput(repoRoot, "symbolic-ref", "--short", "HEAD");
        head.Should().Be(ws.BranchName);
    }

    [Fact]
    public async Task CheckoutNewBranchAsync_fails_if_branch_already_exists()
    {
        var (_, repoRoot, ws) = CreateFreshClone();
        var client = BuildClient(_fixture.TempDir);

        // Create the branch WITHOUT switching to it using "git branch <name>"
        // This is more reliable than checkout -b + checkout main for setup purposes.
        RunGit(repoRoot, "branch", ws.BranchName);

        // Attempt to create the same branch via the GitClient — must fail
        var act = async () => await client.CheckoutNewBranchAsync(ws, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── ResolveDefaultBranchAsync ─────────────────────────────────────────────

    [Fact]
    public async Task ResolveDefaultBranchAsync_returns_main()
    {
        var (_, _, ws) = CreateFreshClone();
        var client = BuildClient(_fixture.TempDir);

        var defaultBranch = await client.ResolveDefaultBranchAsync(ws, CancellationToken.None);

        defaultBranch.Should().Be("main");
    }

    // ── PushAsync — default-branch guard ─────────────────────────────────────

    [Fact]
    public async Task PushAsync_refuses_when_HEAD_is_on_default_branch()
    {
        var (_, repoRoot, ws) = CreateFreshClone();
        // HEAD is already on "main" after clone
        var head = RunGitOutput(repoRoot, "symbolic-ref", "--short", "HEAD");
        head.Should().Be("main"); // confirm precondition

        var client = BuildClient(_fixture.TempDir);

        var act = async () => await client.PushAsync(ws, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*default branch*");
    }

    // ── Add → Commit → Push ───────────────────────────────────────────────────

    [Fact]
    public async Task AddAsync_CommitAsync_PushAsync_succeeds_on_feature_branch()
    {
        var (_, repoRoot, ws) = CreateFreshClone();
        var client = BuildClient(_fixture.TempDir);

        // Create the branch
        await client.CheckoutNewBranchAsync(ws, CancellationToken.None);

        // Write a file
        var newFile = Path.Combine(repoRoot, "newfile.txt");
        await File.WriteAllTextAsync(newFile, "hello\n");

        // Stage it
        await client.AddAsync(ws, ["newfile.txt"], CancellationToken.None);

        // Commit
        await client.CommitAsync(ws, "Add newfile", "Test commit body", CancellationToken.None);

        // Push to the local file-system remote
        await client.PushAsync(ws, CancellationToken.None);

        // Verify: the upstream bare repo now has the branch
        var branches = RunGitOutput(_fixture.UpstreamPath, "branch");
        branches.Should().Contain(ws.BranchName);
    }

    // ── StatusAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task StatusAsync_returns_porcelain_output_for_untracked_file()
    {
        var (_, repoRoot, ws) = CreateFreshClone();
        var client = BuildClient(_fixture.TempDir);

        // Write an untracked file
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "newfile.txt"), "content\n");

        var status = await client.StatusAsync(ws, CancellationToken.None);

        status.Should().Contain("?? newfile.txt");
    }

    // ── DiffAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DiffAsync_returns_diff_against_main()
    {
        var (_, repoRoot, ws) = CreateFreshClone();
        var client = BuildClient(_fixture.TempDir);

        // Create branch, add a file, commit
        await client.CheckoutNewBranchAsync(ws, CancellationToken.None);
        var newFile = Path.Combine(repoRoot, "difftest.txt");
        await File.WriteAllTextAsync(newFile, "hello\n");
        await client.AddAsync(ws, ["difftest.txt"], CancellationToken.None);
        await client.CommitAsync(ws, "Add difftest", "", CancellationToken.None);

        var diff = await client.DiffAsync(ws, "main", CancellationToken.None);

        diff.Should().Contain("difftest.txt");
    }

    // ── GetDiffStatsAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetDiffStatsAsync_counts_files_and_lines()
    {
        var (_, repoRoot, ws) = CreateFreshClone();
        var client = BuildClient(_fixture.TempDir);

        await client.CheckoutNewBranchAsync(ws, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "a.txt"), "l1\nl2\nl3\n");
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "b.txt"), "x\n");
        await client.AddAsync(ws, ["a.txt", "b.txt"], CancellationToken.None);
        await client.CommitAsync(ws, "Add two files", "", CancellationToken.None);

        var stats = await client.GetDiffStatsAsync(ws, "main", CancellationToken.None);

        stats.ChangedFiles.Should().Be(2);
        stats.ChangedLines.Should().Be(4); // 3 added in a.txt + 1 added in b.txt
    }

    [Fact]
    public async Task GetDiffStatsAsync_returns_zero_when_no_changes()
    {
        var (_, _, ws) = CreateFreshClone();
        var client = BuildClient(_fixture.TempDir);

        await client.CheckoutNewBranchAsync(ws, CancellationToken.None);

        var stats = await client.GetDiffStatsAsync(ws, "main", CancellationToken.None);

        stats.ChangedFiles.Should().Be(0);
        stats.ChangedLines.Should().Be(0);
    }

    // ── PushAsync — scope-limit guard ─────────────────────────────────────────

    [Fact]
    public async Task PushAsync_refuses_when_changed_files_exceed_limit()
    {
        var (_, repoRoot, ws) = CreateFreshClone();
        var client = BuildClient(_fixture.TempDir, new ScopeLimitOptions { MaxChangedFiles = 1 });

        await client.CheckoutNewBranchAsync(ws, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "a.txt"), "a\n");
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "b.txt"), "b\n");
        await client.AddAsync(ws, ["a.txt", "b.txt"], CancellationToken.None);
        await client.CommitAsync(ws, "Two files", "", CancellationToken.None);

        var act = async () => await client.PushAsync(ws, CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<ScopeLimitExceededException>()).Which;
        ex.Limit.Should().Be(ScopeLimit.MaxChangedFiles);
        // Upstream must NOT have received the branch.
        var branches = RunGitOutput(_fixture.UpstreamPath, "branch");
        branches.Should().NotContain(ws.BranchName);
    }

    [Fact]
    public async Task PushAsync_refuses_when_changed_lines_exceed_limit()
    {
        var (_, repoRoot, ws) = CreateFreshClone();
        var client = BuildClient(_fixture.TempDir,
            new ScopeLimitOptions { MaxChangedFiles = 100, MaxChangedLines = 2 });

        await client.CheckoutNewBranchAsync(ws, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "a.txt"), "l1\nl2\nl3\nl4\n");
        await client.AddAsync(ws, ["a.txt"], CancellationToken.None);
        await client.CommitAsync(ws, "Four lines", "", CancellationToken.None);

        var act = async () => await client.PushAsync(ws, CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<ScopeLimitExceededException>()).Which;
        ex.Limit.Should().Be(ScopeLimit.MaxChangedLines);
    }

    [Fact]
    public async Task PushAsync_succeeds_when_within_limits()
    {
        var (_, repoRoot, ws0) = CreateFreshClone();
        // Unique branch so this test's push does not collide with the other pushing test
        // that shares the same TempRepoFixture upstream.
        var ws = ws0 with { BranchName = "agent/within-limits-" + Guid.NewGuid().ToString("N")[..8] };
        var client = BuildClient(_fixture.TempDir,
            new ScopeLimitOptions { MaxChangedFiles = 10, MaxChangedLines = 100 });

        await client.CheckoutNewBranchAsync(ws, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(repoRoot, "newfile.txt"), "hello\n");
        await client.AddAsync(ws, ["newfile.txt"], CancellationToken.None);
        await client.CommitAsync(ws, "Add newfile", "", CancellationToken.None);

        await client.PushAsync(ws, CancellationToken.None);

        var branches = RunGitOutput(_fixture.UpstreamPath, "branch");
        branches.Should().Contain(ws.BranchName);
    }
}

/// <summary>
/// xUnit class fixture that creates a bare upstream repo + populates it with one commit,
/// so each test can clone from it cheaply.
/// </summary>
public sealed class TempRepoFixture : IDisposable
{
    public string TempDir { get; }
    public string UpstreamPath { get; }
    public string UpstreamUrl { get; }

    public TempRepoFixture()
    {
        TempDir = Path.Combine(Path.GetTempPath(), "git-client-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(TempDir);

        UpstreamPath = Path.Combine(TempDir, "upstream.git");
        UpstreamUrl = UpstreamPath; // file path acts as a local URL

        // Configure git globally to trust all directories.
        // This avoids "unsafe repository" errors on Windows when the temp dir is owned
        // by a different user identity (common in sandboxed CI environments).
        RunGit(TempDir, "config", "--global", "safe.directory", "*");

        // 1. Create bare upstream
        RunGit(TempDir, "init", "--bare", "--initial-branch=main", "upstream.git");

        // 2. Create a temporary working clone to make the first commit
        var workingDir = Path.Combine(TempDir, "init-work");
        RunGit(TempDir, "clone", UpstreamPath, workingDir);
        RunGit(workingDir, "config", "user.email", "test@example.com");
        RunGit(workingDir, "config", "user.name", "Test");

        // 3. Add initial commit (disable GPG signing to avoid dependency on gpg installation)
        RunGit(workingDir, "config", "commit.gpgsign", "false");
        var readmeFile = Path.Combine(workingDir, "README.md");
        File.WriteAllText(readmeFile, "# Test repo\n");
        RunGit(workingDir, "add", "README.md");
        RunGit(workingDir, "commit", "-m", "Initial commit");
        RunGit(workingDir, "push", "origin", "main");

        // 4. origin/HEAD is set automatically because the bare repo has only one branch (main).
    }

    private static void RunGit(string cwd, params string[] args)
    {
        using var p = new System.Diagnostics.Process();
        p.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) p.StartInfo.ArgumentList.Add(a);
        p.Start();
        p.WaitForExit();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(TempDir))
            {
                // On Windows, git objects can be read-only; force delete
                SetAttributesNormal(new DirectoryInfo(TempDir));
                Directory.Delete(TempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup — don't fail the test run on cleanup errors
        }
    }

    private static void SetAttributesNormal(DirectoryInfo dir)
    {
        foreach (var subDir in dir.GetDirectories())
            SetAttributesNormal(subDir);
        foreach (var file in dir.GetFiles())
            file.Attributes = FileAttributes.Normal;
    }
}
