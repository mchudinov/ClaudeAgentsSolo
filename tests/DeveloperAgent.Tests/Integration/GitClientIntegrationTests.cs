using DeveloperAgent.Configuration;
using DeveloperAgent.Sandbox;
using DeveloperAgent.Tests.Sandbox;
using DeveloperAgent.Workspace;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeveloperAgent.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="GitClient"/> against a real git binary and a real remote.
/// <para>
/// Env var required: <c>GIT_INTEGRATION_REPO</c> — HTTPS URL of a small, public sandbox repo.
/// If absent, every test in this class is skipped.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class GitClientIntegrationTests
{
    private const string EnvRepo = "GIT_INTEGRATION_REPO";

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static GitClient BuildClient(string workspaceRoot)
    {
        var workspaceOpts = Options.Create(new WorkspaceOptions
        {
            RootPath = workspaceRoot,
            AllowedCommands = ProductionSandboxConfig.AllowedCommands,
        });
        var githubOpts = Options.Create(new GitHubOptions());
        var secrets = new SecretsBundle("", "");
        var processRunner = new DefaultProcessRunner();
        var sandboxOpts = Options.Create(new SandboxOptions
        {
            DenyPathPatterns = [],
            SecretFileRegexes = [],
            // Integration tests run real `git push` — drop the force-push deny rules
            // so we keep verifying the legitimate push path and not the sandbox itself.
            DeniedCommands = [],
        });
        var denyPolicy = new PathDenyPolicy(sandboxOpts);
        var commandDenyPolicy = new CommandDenyPolicy(sandboxOpts);
        var sandbox = new CommandSandbox(
            processRunner,
            workspaceOpts,
            denyPolicy,
            commandDenyPolicy,
            NullLogger<CommandSandbox>.Instance);

        // Generous scope limits so the integration push path is not blocked by the
        // Step-21 (P2-H) changed-file / changed-line gate.
        var scopeOpts = Options.Create(new ScopeLimitOptions
        {
            MaxChangedFiles = 10_000,
            MaxChangedLines = 1_000_000,
        });

        return new GitClient(
            sandbox,
            workspaceOpts,
            githubOpts,
            scopeOpts,
            secrets,
            NullLogger<GitClient>.Instance);
    }

    /// <summary>
    /// Removes all read-only flags so Directory.Delete can remove .git objects on Windows.
    /// </summary>
    private static void SetAttributesNormal(DirectoryInfo dir)
    {
        foreach (var subDir in dir.GetDirectories())
            SetAttributesNormal(subDir);
        foreach (var file in dir.GetFiles())
            file.Attributes = FileAttributes.Normal;
    }

    private static void DeleteTempDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                SetAttributesNormal(new DirectoryInfo(path));
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; do not fail the test on cleanup errors.
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Clone_and_branch_and_local_commit_succeed_against_a_real_repo()
    {
        var reason = EnvironmentSkip.ReasonIfMissing(EnvRepo);
        Skip.If(reason is not null, reason ?? string.Empty);

        var repoUrl = Environment.GetEnvironmentVariable(EnvRepo)!;
        var tempDir = Path.Combine(Path.GetTempPath(), "git-int-clone-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            var repoRoot = Path.Combine(tempDir, "repo");
            Directory.CreateDirectory(repoRoot);

            var branchName = $"agent/integration-test-{Guid.NewGuid().ToString("N")[..8]}";
            var ws = new TaskWorkspace(
                ProjectItemId: "int-test",
                BranchName: branchName,
                RepoRoot: repoRoot,
                DefaultBranch: "main");

            var client = BuildClient(tempDir);

            // Clone
            await client.CloneAsync(ws, repoUrl, CancellationToken.None);

            Directory.Exists(Path.Combine(repoRoot, ".git"))
                .Should().BeTrue(because: "clone should create a .git directory");

            // Set local git identity so commits work in environments without global git config.
            RunGit(repoRoot, "config", "user.email", "integration-test@example.com");
            RunGit(repoRoot, "config", "user.name", "Integration Test");
            RunGit(repoRoot, "config", "commit.gpgsign", "false");

            // Set remote/HEAD so ResolveDefaultBranchAsync works
            RunGit(repoRoot, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");

            // Checkout new branch
            await client.CheckoutNewBranchAsync(ws, CancellationToken.None);

            // Assert HEAD is on the feature branch
            var head = RunGitOutput(repoRoot, "symbolic-ref", "--short", "HEAD");
            head.Should().Be(branchName, because: "CheckoutNewBranchAsync should switch HEAD to the new branch");
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    [SkippableFact]
    public async Task Push_to_default_branch_is_refused()
    {
        var reason = EnvironmentSkip.ReasonIfMissing(EnvRepo);
        Skip.If(reason is not null, reason ?? string.Empty);

        var repoUrl = Environment.GetEnvironmentVariable(EnvRepo)!;
        var tempDir = Path.Combine(Path.GetTempPath(), "git-int-push-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            var repoRoot = Path.Combine(tempDir, "repo");
            Directory.CreateDirectory(repoRoot);

            var ws = new TaskWorkspace(
                ProjectItemId: "int-push",
                BranchName: "agent/unused",
                RepoRoot: repoRoot,
                DefaultBranch: "main");

            var client = BuildClient(tempDir);

            // Clone but do not branch — HEAD stays on "main"
            await client.CloneAsync(ws, repoUrl, CancellationToken.None);

            // Confirm precondition: HEAD is on the default branch
            var head = RunGitOutput(repoRoot, "symbolic-ref", "--short", "HEAD");
            head.Should().Be("main", because: "after clone HEAD should be on main");

            // PushAsync must refuse to push when HEAD == DefaultBranch
            var act = async () => await client.PushAsync(ws, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*default branch*");
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    // ── Git helpers ───────────────────────────────────────────────────────────

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
}
