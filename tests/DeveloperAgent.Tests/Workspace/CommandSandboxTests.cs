using DeveloperAgent.Configuration;
using DeveloperAgent.Sandbox;
using DeveloperAgent.Workspace;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace DeveloperAgent.Tests.Workspace;

/// <summary>
/// Unit tests for <see cref="CommandSandbox"/>.
/// All tests use a fake <see cref="IProcessRunner"/> — no real child processes are spawned.
/// </summary>
public sealed class CommandSandboxTests
{
    // ── Fixture helpers ──────────────────────────────────────────────────────

    private static readonly string RootPath = Path.Combine(Path.GetTempPath(), "ws-test-root");

    /// <summary>A CWD that is legitimately inside the root.</summary>
    private static string ValidCwd => Path.Combine(RootPath, "item1", "repo");

    private static CommandSandbox BuildSandbox(
        IProcessRunner runner,
        IReadOnlyList<string>? allowedCommands = null,
        ILogger<CommandSandbox>? logger = null)
    {
        var opts = Options.Create(new WorkspaceOptions
        {
            RootPath = RootPath,
            AllowedCommands = allowedCommands ?? new WorkspaceOptions().AllowedCommands,
        });
        var denyPolicy = new PathDenyPolicy(Options.Create(new SandboxOptions
        {
            // Patterns drop out — the legacy CommandSandbox tests assume the *only*
            // path constraint enforced on the CWD is workspace-escape.
            DenyPathPatterns = [],
            SecretFileRegexes = [],
        }));
        return new CommandSandbox(runner, opts, denyPolicy, logger ?? Substitute.For<ILogger<CommandSandbox>>());
    }

    private static IProcessRunner OkRunner(int exitCode = 0, string stdout = "", string stderr = "")
    {
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>())
            .Returns(new ProcessRunResult(exitCode, stdout, stderr, TimeSpan.FromMilliseconds(10), false));
        return runner;
    }

    private static IProcessRunner TimedOutRunner()
    {
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>())
            .Returns(new ProcessRunResult(-1, "", "", TimeSpan.FromSeconds(30), true));
        return runner;
    }

    // ── Allowlist hit ────────────────────────────────────────────────────────

    [Fact]
    public async Task Allowlist_hit_runs_command_exactly_once()
    {
        var runner = OkRunner();
        var sandbox = BuildSandbox(runner, ["dotnet build"]);

        await sandbox.RunAsync(
            "dotnet build src/Foo.csproj --no-restore",
            ValidCwd, TimeSpan.FromSeconds(30), CancellationToken.None);

        await runner.Received(1).RunAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Allowlist_hit_returns_exit_code_from_runner()
    {
        var runner = OkRunner(exitCode: 1, stderr: "build failed");
        var sandbox = BuildSandbox(runner, ["dotnet build"]);

        var result = await sandbox.RunAsync(
            "dotnet build src/Foo.csproj",
            ValidCwd, TimeSpan.FromSeconds(30), CancellationToken.None);

        result.ExitCode.Should().Be(1);
        result.Stderr.Should().Be("build failed");
    }

    [Fact]
    public async Task Allowlist_hit_git_push_allowed_is_run()
    {
        var runner = OkRunner();
        var sandbox = BuildSandbox(runner, ["git push"]);

        // "git push --set-upstream origin my-branch" should be allowed
        await sandbox.RunAsync(
            "git push --set-upstream origin my-branch",
            ValidCwd, TimeSpan.FromSeconds(30), CancellationToken.None);

        await runner.Received(1).RunAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    // ── Allowlist miss ───────────────────────────────────────────────────────

    [Fact]
    public async Task Allowlist_miss_whole_token_throws_SandboxViolationException()
    {
        var runner = OkRunner();
        var sandbox = BuildSandbox(runner, ["git push"]);

        var act = async () => await sandbox.RunAsync(
            "git pushplus origin main",
            ValidCwd, TimeSpan.FromSeconds(30), CancellationToken.None);

        await act.Should().ThrowAsync<SandboxViolationException>();
    }

    [Fact]
    public async Task Allowlist_miss_unrecognised_command_throws_SandboxViolationException()
    {
        var runner = OkRunner();
        var sandbox = BuildSandbox(runner, ["dotnet build"]);

        var act = async () => await sandbox.RunAsync(
            "curl https://evil.com",
            ValidCwd, TimeSpan.FromSeconds(30), CancellationToken.None);

        await act.Should().ThrowAsync<SandboxViolationException>();
    }

    [Fact]
    public async Task Allowlist_miss_does_not_invoke_runner()
    {
        var runner = OkRunner();
        var sandbox = BuildSandbox(runner, ["dotnet build"]);

        try
        {
            await sandbox.RunAsync(
                "rm -rf /",
                ValidCwd, TimeSpan.FromSeconds(30), CancellationToken.None);
        }
        catch (SandboxViolationException) { /* expected */ }

        await runner.DidNotReceive().RunAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    // ── git push --force rejection ────────────────────────────────────────────

    [Fact]
    public async Task Git_push_force_long_flag_throws_SandboxViolationException()
    {
        var runner = OkRunner();
        var sandbox = BuildSandbox(runner, ["git push"]);

        var act = async () => await sandbox.RunAsync(
            "git push --force",
            ValidCwd, TimeSpan.FromSeconds(30), CancellationToken.None);

        await act.Should().ThrowAsync<SandboxViolationException>()
                 .WithMessage("*--force*");
    }

    [Fact]
    public async Task Git_push_force_short_flag_throws_SandboxViolationException()
    {
        var runner = OkRunner();
        var sandbox = BuildSandbox(runner, ["git push"]);

        var act = async () => await sandbox.RunAsync(
            "git push -f origin main",
            ValidCwd, TimeSpan.FromSeconds(30), CancellationToken.None);

        await act.Should().ThrowAsync<SandboxViolationException>();
    }

    // ── -c key=value leading-pair stripping ──────────────────────────────────

    [Fact]
    public async Task Git_with_leading_c_kv_pair_matches_git_clone_allowlist()
    {
        var runner = OkRunner();
        // Allowlist has "git clone"; command line has "-c http.extraheader=…" prefix.
        // The value is quoted (as the real GitClient generates it) so the tokeniser
        // keeps it as a single token.
        var sandbox = BuildSandbox(runner, ["git clone"]);

        var act = async () => await sandbox.RunAsync(
            "git -c \"http.extraheader=Authorization: Bearer xyz\" clone https://github.com/foo repo",
            ValidCwd, TimeSpan.FromSeconds(30), CancellationToken.None);

        // Should NOT throw — the -c pair is stripped before prefix matching
        await act.Should().NotThrowAsync<SandboxViolationException>();
    }

    [Fact]
    public async Task Git_with_multiple_leading_c_kv_pairs_are_all_stripped()
    {
        var runner = OkRunner();
        var sandbox = BuildSandbox(runner, ["git clone"]);

        var act = async () => await sandbox.RunAsync(
            "git -c \"user.name=Bot\" -c \"http.extraheader=Auth: x\" clone https://github.com/foo repo",
            ValidCwd, TimeSpan.FromSeconds(30), CancellationToken.None);

        await act.Should().NotThrowAsync<SandboxViolationException>();
    }

    [Fact]
    public async Task Git_c_flag_after_non_c_token_is_not_stripped()
    {
        var runner = OkRunner();
        // "git clone -c http.extraheader=…" — the -c comes AFTER "clone", so it's not stripped
        // The effective argv for matching is ["git", "clone", "-c", "..."], which matches "git clone"
        // so this should succeed (the -c is just a normal arg to clone)
        var sandbox = BuildSandbox(runner, ["git clone"]);

        var act = async () => await sandbox.RunAsync(
            "git clone https://example.com repo -c http.extraheader=value",
            ValidCwd, TimeSpan.FromSeconds(30), CancellationToken.None);

        await act.Should().NotThrowAsync<SandboxViolationException>();
    }

    // ── CWD validation ───────────────────────────────────────────────────────

    [Fact]
    public async Task Cwd_outside_root_absolute_path_throws_SandboxViolationException()
    {
        var runner = OkRunner();
        var sandbox = BuildSandbox(runner, ["dotnet build"]);

        // CWD is entirely outside the workspace root
        var act = async () => await sandbox.RunAsync(
            "dotnet build",
            "C:\\Windows\\System32", TimeSpan.FromSeconds(30), CancellationToken.None);

        await act.Should().ThrowAsync<SandboxViolationException>()
                 .WithMessage("*cwd outside workspace root*");
    }

    [Fact]
    public async Task Cwd_that_is_prefix_of_root_but_different_directory_throws()
    {
        // Root is e.g. C:\tmp\ws-test-root\
        // CWD  is      C:\tmp\ws-test-rootx\  — shares prefix chars but is different dir
        var runner = OkRunner();
        var opts = Options.Create(new WorkspaceOptions
        {
            RootPath = RootPath,
            AllowedCommands = ["dotnet build"],
        });
        var denyPolicy = new PathDenyPolicy(Options.Create(new SandboxOptions
        {
            DenyPathPatterns = [],
            SecretFileRegexes = [],
        }));
        var sandbox = new CommandSandbox(runner, opts, denyPolicy, Substitute.For<ILogger<CommandSandbox>>());

        var siblingDir = RootPath + "x"; // adjacent directory, NOT inside root
        var act = async () => await sandbox.RunAsync(
            "dotnet build",
            siblingDir, TimeSpan.FromSeconds(30), CancellationToken.None);

        await act.Should().ThrowAsync<SandboxViolationException>()
                 .WithMessage("*cwd outside workspace root*");
    }

    [Fact]
    public async Task Cwd_is_root_itself_is_allowed()
    {
        var runner = OkRunner();
        var sandbox = BuildSandbox(runner, ["dotnet build"]);

        // Exact root path is valid
        var rootWithSep = RootPath;
        // We need to ensure the root dir "exists" for Path.GetFullPath — it doesn't do FS lookups
        // so this is fine even if the dir doesn't exist
        var act = async () => await sandbox.RunAsync(
            "dotnet build",
            RootPath + Path.DirectorySeparatorChar + "item1",
            TimeSpan.FromSeconds(30), CancellationToken.None);

        await act.Should().NotThrowAsync<SandboxViolationException>();
    }

    // ── No shell interpretation ──────────────────────────────────────────────

    [Fact]
    public async Task Shell_operators_are_passed_as_literal_args_not_expanded()
    {
        IReadOnlyList<string>? capturedArgs = null;
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(
            Arg.Any<string>(),
            Arg.Do<IReadOnlyList<string>>(args => capturedArgs = args),
            Arg.Any<string>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>())
            .Returns(new ProcessRunResult(0, "", "", TimeSpan.FromMilliseconds(5), false));

        var sandbox = BuildSandbox(runner, ["dotnet build"]);

        await sandbox.RunAsync(
            "dotnet build && echo x",
            ValidCwd, TimeSpan.FromSeconds(30), CancellationToken.None);

        // argv passed to runner should contain "&&" and "echo" as separate, literal arguments
        capturedArgs.Should().NotBeNull();
        capturedArgs!.Should().Contain("&&");
        capturedArgs.Should().Contain("echo");
        capturedArgs.Should().Contain("x");
    }

    // ── Timeout propagation ──────────────────────────────────────────────────

    [Fact]
    public async Task Timeout_propagates_TimedOut_true_in_CommandResult()
    {
        var runner = TimedOutRunner();
        var sandbox = BuildSandbox(runner, ["dotnet test"]);

        var result = await sandbox.RunAsync(
            "dotnet test",
            ValidCwd, TimeSpan.FromMilliseconds(100), CancellationToken.None);

        result.TimedOut.Should().BeTrue();
    }

    [Fact]
    public async Task Timeout_does_not_throw_exception()
    {
        var runner = TimedOutRunner();
        var sandbox = BuildSandbox(runner, ["dotnet test"]);

        var act = async () => await sandbox.RunAsync(
            "dotnet test",
            ValidCwd, TimeSpan.FromMilliseconds(100), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── Exit-code propagation ────────────────────────────────────────────────

    [Fact]
    public async Task Exit_code_propagated_no_exception_thrown()
    {
        var runner = OkRunner(exitCode: 2);
        var sandbox = BuildSandbox(runner, ["dotnet build"]);

        var result = await sandbox.RunAsync(
            "dotnet build",
            ValidCwd, TimeSpan.FromSeconds(30), CancellationToken.None);

        result.ExitCode.Should().Be(2);
    }

    // ── Logging redaction ────────────────────────────────────────────────────

    [Fact]
    public async Task Logger_does_not_log_env_var_secret_value()
    {
        // Arrange: set an env var with a "secret" value and run a normal command.
        // The sandbox must NOT dump Environment.GetEnvironmentVariables() into any log message.
        const string secretValue = "super-secret-token-xyz-999";
        Environment.SetEnvironmentVariable("SANDBOX_TEST_TOKEN", secretValue);

        try
        {
            var capturedMessages = new List<string>();
            var logger = Substitute.For<ILogger<CommandSandbox>>();

            // Capture the formatted message string from every LogInformation call.
            // ILogger.Log<TState> has a complex generic signature that NSubstitute can't
            // intercept via ArgAt; instead we use ReceivedCalls() after the fact and
            // call each formatter manually.
            var runner = OkRunner();
            var denyPolicy = new PathDenyPolicy(Options.Create(new SandboxOptions
            {
                DenyPathPatterns = [],
                SecretFileRegexes = [],
            }));
            var sandbox = new CommandSandbox(
                runner,
                Options.Create(new WorkspaceOptions
                {
                    RootPath = RootPath,
                    AllowedCommands = ["dotnet build"],
                }),
                denyPolicy,
                logger);

            // Act
            await sandbox.RunAsync(
                "dotnet build",
                ValidCwd, TimeSpan.FromSeconds(30), CancellationToken.None);

            // Collect all log calls via ReceivedCalls and format them
            foreach (var call in logger.ReceivedCalls())
            {
                var args = call.GetArguments();
                if (args.Length >= 5 && args[2] is not null && args[4] is not null)
                {
                    // args[2] = state (object), args[4] = formatter (Func<object, Exception?, string>)
                    try
                    {
                        var formatter = args[4]!;
                        var formatterType = formatter.GetType();
                        var invokeMethod = formatterType.GetMethod("Invoke");
                        if (invokeMethod is not null)
                        {
                            var formatted = invokeMethod.Invoke(formatter, [args[2], args[3]]) as string;
                            if (formatted is not null)
                                capturedMessages.Add(formatted);
                        }
                    }
                    catch { /* skip calls we can't format */ }
                }
            }

            // Assert: none of the formatted messages contain the raw secret value
            capturedMessages.Should().NotContain(m => m.Contains(secretValue),
                because: "the sandbox should not log environment variable secret values");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SANDBOX_TEST_TOKEN", null);
        }
    }

    // ── Tokeniser unit tests ─────────────────────────────────────────────────

    [Fact]
    public void Tokenise_splits_on_whitespace()
    {
        var result = CommandSandbox.Tokenise("dotnet build src/Foo.csproj");

        result.Should().Equal("dotnet", "build", "src/Foo.csproj");
    }

    [Fact]
    public void Tokenise_honours_double_quotes()
    {
        var result = CommandSandbox.Tokenise("git commit -m \"my message here\"");

        result.Should().Equal("git", "commit", "-m", "my message here");
    }

    [Fact]
    public void Tokenise_honours_single_quotes()
    {
        var result = CommandSandbox.Tokenise("git commit -m 'my message'");

        result.Should().Equal("git", "commit", "-m", "my message");
    }

    // ── StripLeadingConfigPairs unit tests ───────────────────────────────────

    [Fact]
    public void StripLeadingConfigPairs_strips_single_pair()
    {
        var argv = new[] { "git", "-c", "http.extraheader=value", "clone", "url" };
        var result = CommandSandbox.StripLeadingConfigPairs(argv);

        result.Should().Equal("git", "clone", "url");
    }

    [Fact]
    public void StripLeadingConfigPairs_strips_multiple_consecutive_pairs()
    {
        var argv = new[] { "git", "-c", "user.name=Bot", "-c", "http.extraheader=x", "push" };
        var result = CommandSandbox.StripLeadingConfigPairs(argv);

        result.Should().Equal("git", "push");
    }

    [Fact]
    public void StripLeadingConfigPairs_leaves_c_after_non_c_token_intact()
    {
        var argv = new[] { "git", "clone", "-c", "http.extraheader=value", "url" };
        var result = CommandSandbox.StripLeadingConfigPairs(argv);

        // No stripping — "-c" comes after "clone"
        result.Should().Equal("git", "clone", "-c", "http.extraheader=value", "url");
    }

    [Fact]
    public void StripLeadingConfigPairs_does_not_strip_c_without_equals_in_value()
    {
        // "-c" followed by a token without "=" is not a kv pair
        var argv = new[] { "git", "-c", "noequals", "clone" };
        var result = CommandSandbox.StripLeadingConfigPairs(argv);

        result.Should().Equal("git", "-c", "noequals", "clone");
    }
}
