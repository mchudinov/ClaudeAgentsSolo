using System.Text.Json;
using System.Text.Json.Nodes;
using DeveloperAgent.Agent;
using DeveloperAgent.Agent.Tools;
using DeveloperAgent.GitHub;
using DeveloperAgent.Workspace;

namespace DeveloperAgent.Tests.Agent.Tools;

/// <summary>Unit tests for <see cref="ShellRunTool"/>.</summary>
public sealed class ShellRunToolTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "shell-run-tests");
    private readonly ICommandSandbox _sandbox = Substitute.For<ICommandSandbox>();
    private readonly ShellRunTool _tool;
    private readonly ToolContext _ctx;

    public ShellRunToolTests()
    {
        _tool = new ShellRunTool(_sandbox);
        var ws = new TaskWorkspace("item-1", "branch-1", Root, "main");
        _ctx = new ToolContext(new AgentRunState(), ws,
            new ProjectItem("pid", "cid", 1, "T", "B", ProjectState.InProgress));
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
        _sandbox.RunAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns<CommandResult>(_ => throw new SandboxViolationException("rm -rf not allowed"));

        var act = async () => await _tool.InvokeAsync(
            JsonNode.Parse("{\"command\":\"rm -rf /\"}")!, _ctx, CancellationToken.None);

        await act.Should().ThrowAsync<SandboxViolationException>();
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
