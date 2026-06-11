using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Agent.Sandbox.Tests;

/// <summary>Unit tests for <see cref="ShellRunTool"/>.</summary>
public sealed class ShellRunToolTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "shell-run-tests");
    private static readonly WorkspaceOptions Workspace = new()
    {
        RootPath = "/workspace",
        AllowedCommands = ["dotnet", "git", "echo"],
    };
    private readonly ICommandSandbox _sandbox = Substitute.For<ICommandSandbox>();
    private readonly ShellRunTool _tool;
    private readonly IToolContext _ctx;

    public ShellRunToolTests()
    {
        _tool = new ShellRunTool(_sandbox, Options.Create(Workspace));
        // ShellRunTool reads only IToolContext.WorkspaceRoot — a slim test double suffices
        // (the host's concrete ToolContext carries policy fields the tool never touches).
        _ctx = new TestToolContext(Root);
    }

    private void SetupSandbox(int exitCode = 0, string stdout = "", string stderr = "", bool timedOut = false)
    {
        _sandbox.RunAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(new CommandResult(exitCode, stdout, stderr, TimeSpan.FromMilliseconds(50), timedOut));
    }

    [Fact]
    public async Task Returns_command_result_on_success()
    {
        SetupSandbox(exitCode: 0, stdout: "Build succeeded.");

        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{\"command\":\"dotnet build\"}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(result.Content)!;
        payload["exit_code"].GetInt32().Should().Be(0);
        payload["stdout"].GetString().Should().Be("Build succeeded.");
    }

    [Fact]
    public async Task Returns_timed_out_true_when_sandbox_reports_timeout()
    {
        SetupSandbox(timedOut: true);

        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{\"command\":\"dotnet build\"}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(result.Content)!;
        payload["timed_out"].GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Sandbox_violation_rethrows_exception()
    {
        // A SandboxViolationException now means only a workspace escape (cwd outside the root) or
        // an empty/malformed segment — the genuinely fatal cases the sandbox raises. The tool
        // re-throws so the host runner ends the run with a SandboxViolation outcome.
        _sandbox.RunAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns<CommandResult>(_ => throw new SandboxViolationException("cwd outside workspace root"));

        var act = async () => await _tool.InvokeAsync(
            JsonNode.Parse("{\"command\":\"dotnet build\"}")!, _ctx, CancellationToken.None);

        await act.Should().ThrowAsync<SandboxViolationException>();
    }

    [Fact]
    public async Task Deny_rule_hit_returns_recoverable_error_result_instead_of_throwing()
    {
        // A deny-rule hit (blind --force, curl, secret manipulation, …) is non-fatal: the command
        // never ran, so the tool hands the deny reason back to the model as an error result and the
        // run continues, letting the model adapt (e.g. use the allowed --force-with-lease). It is
        // NOT re-thrown like a workspace escape, and NOT wrapped in the generic "Command execution
        // error:" prefix reserved for unexpected exceptions.
        _sandbox.RunAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns<CommandResult>(_ => throw new CommandDeniedException(
                "command 'git push --force' is denied by sandbox policy: deny rule 'no-git-force-push'"));

        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{\"command\":\"git push --force\"}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("denied");
        result.Content.Should().NotContain("Command execution error");
    }

    [Fact]
    public async Task Allowlist_miss_returns_recoverable_error_result_instead_of_throwing()
    {
        // An allowlist miss is non-fatal: the tool hands it back to the model as an error result
        // (so the run continues and the model retries with an allowed command), rather than
        // re-throwing like a deny-rule hit. The command never ran, so the boundary is intact.
        _sandbox.RunAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns<CommandResult>(_ => throw new CommandNotAllowedException(
                "Command 'echo hi' is not in the workspace allowlist. Allowed commands: dotnet, git."));

        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{\"command\":\"echo hi\"}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("Allowed commands");
        // The dedicated recoverable path returns the guidance verbatim — NOT wrapped in the
        // generic "Command execution error:" prefix reserved for unexpected exceptions.
        result.Content.Should().NotContain("Command execution error");
    }

    [Fact]
    public void Description_lists_the_allowed_commands()
    {
        // The model is told the allowlist up-front so it stops guessing — an un-listed `echo`
        // guessed to be fine is exactly what used to kill runs.
        _tool.Description.Should().Contain("dotnet").And.Contain("git").And.Contain("echo");
    }

    [Fact]
    public async Task Non_sandbox_exception_returns_error_result()
    {
        _sandbox.RunAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns<CommandResult>(_ => throw new IOException("disk full"));

        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{\"command\":\"dotnet build\"}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("disk full");
    }

    [Fact]
    public async Task Returns_error_for_missing_command()
    {
        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Timeout_clamped_to_maximum()
    {
        TimeSpan? capturedTimeout = null;
        _sandbox.RunAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Do<TimeSpan>(t => capturedTimeout = t),
            Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(new CommandResult(0, "", "", TimeSpan.Zero, false));

        await _tool.InvokeAsync(
            JsonNode.Parse("{\"command\":\"dotnet build\",\"timeout_seconds\":99999}")!, _ctx, CancellationToken.None);

        capturedTimeout.Should().Be(TimeSpan.FromSeconds(1200));
    }

    [Fact]
    public async Task Requests_container_isolation()
    {
        bool? capturedIsolate = null;
        _sandbox.RunAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>(),
            Arg.Do<bool>(b => capturedIsolate = b))
            .Returns(new CommandResult(0, "", "", TimeSpan.Zero, false));

        await _tool.InvokeAsync(
            JsonNode.Parse("{\"command\":\"dotnet build\"}")!, _ctx, CancellationToken.None);

        capturedIsolate.Should().BeTrue(because: "shell_run must request container isolation");
    }
}
