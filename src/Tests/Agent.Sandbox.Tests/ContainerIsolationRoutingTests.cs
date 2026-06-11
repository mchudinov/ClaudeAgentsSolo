using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agent.Sandbox.Tests;

/// <summary>
/// Tests that <see cref="CommandSandbox"/> routes an <c>isolate: true</c> command through
/// <see cref="IContainerRuntime"/> when isolation is enabled, and that the three acceptance
/// scenarios (success, timeout, resource-cap breach) surface correctly via the stub. The
/// allowlist / deny / cwd validation runs identically regardless of isolation.
/// </summary>
public sealed class ContainerIsolationRoutingTests
{
    private static readonly string RootPath = Path.Combine(Path.GetTempPath(), "container-iso-root");
    private static string ValidCwd => Path.Combine(RootPath, "item1", "repo");

    private static CommandSandbox BuildSandbox(
        IContainerRuntime? containerRuntime,
        bool containerEnabled,
        IProcessRunner? hostRunner = null)
    {
        var workspaceOpts = Options.Create(new WorkspaceOptions
        {
            RootPath = RootPath,
            AllowedCommands = ["dotnet build", "dotnet test"],
        });
        var sandboxOpts = Options.Create(new SandboxOptions
        {
            DenyPathPatterns = [],
            SecretFileRegexes = [],
            DeniedCommands = [],
        });
        var containerOpts = Options.Create(new ContainerRuntimeOptions
        {
            Enabled = containerEnabled,
            MountPath = "/workspace",
        });
        var runner = hostRunner ?? Substitute.For<IProcessRunner>();
        return new CommandSandbox(
            runner,
            workspaceOpts,
            new PathDenyPolicy(sandboxOpts),
            new CommandDenyPolicy(sandboxOpts),
            NullLogger<CommandSandbox>.Instance,
            containerRuntime,
            containerOpts);
    }

    [Fact]
    public async Task Isolated_command_runs_in_container_on_success()
    {
        var stub = StubContainerRuntime.Success("Build succeeded.");
        var hostRunner = Substitute.For<IProcessRunner>();
        var sandbox = BuildSandbox(stub, containerEnabled: true, hostRunner: hostRunner);

        var result = await sandbox.RunAsync(
            "dotnet build", ValidCwd, TimeSpan.FromSeconds(30), CancellationToken.None, isolate: true);

        result.ExitCode.Should().Be(0);
        result.Stdout.Should().Be("Build succeeded.");
        stub.InvocationCount.Should().Be(1);
        // Host runner must NOT be used when the container handled the command.
        await hostRunner.DidNotReceive().RunAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Isolated_command_surfaces_timeout()
    {
        var stub = StubContainerRuntime.Timeout();
        var sandbox = BuildSandbox(stub, containerEnabled: true);

        var result = await sandbox.RunAsync(
            "dotnet test", ValidCwd, TimeSpan.FromSeconds(5), CancellationToken.None, isolate: true);

        result.TimedOut.Should().BeTrue();
        result.ExitCode.Should().Be(-1);
        // The request timeout is threaded through to the runtime.
        stub.LastRequest!.Timeout.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Isolated_command_surfaces_resource_cap_breach()
    {
        var stub = StubContainerRuntime.ResourceCapBreach();
        var sandbox = BuildSandbox(stub, containerEnabled: true);

        var result = await sandbox.RunAsync(
            "dotnet test", ValidCwd, TimeSpan.FromSeconds(30), CancellationToken.None, isolate: true);

        result.TimedOut.Should().BeFalse();
        result.ExitCode.Should().Be(137);
        result.Stderr.Should().Contain("memory limit exceeded");
    }

    [Fact]
    public async Task Isolated_command_passes_workspace_mount_and_container_cwd()
    {
        var stub = StubContainerRuntime.Success();
        var sandbox = BuildSandbox(stub, containerEnabled: true);

        await sandbox.RunAsync(
            "dotnet build", ValidCwd, TimeSpan.FromSeconds(30), CancellationToken.None, isolate: true);

        stub.LastRequest.Should().NotBeNull();
        stub.LastRequest!.WorkspacePath.Should().Be(RootPath);
        stub.LastRequest.MountPath.Should().Be("/workspace");
        stub.LastRequest.WorkingDirectoryInContainer.Should().Be("/workspace/item1/repo");
        stub.LastRequest.Executable.Should().Be("dotnet");
        stub.LastRequest.Arguments.Should().Equal("build");
    }

    [Fact]
    public async Task Isolation_disabled_runs_on_host_even_when_requested()
    {
        var stub = StubContainerRuntime.Success();
        var hostRunner = Substitute.For<IProcessRunner>();
        hostRunner.RunAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessRunResult(0, "host", "", TimeSpan.FromMilliseconds(5), false));
        var sandbox = BuildSandbox(stub, containerEnabled: false, hostRunner: hostRunner);

        var result = await sandbox.RunAsync(
            "dotnet build", ValidCwd, TimeSpan.FromSeconds(30), CancellationToken.None, isolate: true);

        result.Stdout.Should().Be("host");
        stub.InvocationCount.Should().Be(0);
        await hostRunner.Received(1).RunAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Non_isolated_command_runs_on_host_even_when_enabled()
    {
        var stub = StubContainerRuntime.Success();
        var hostRunner = Substitute.For<IProcessRunner>();
        hostRunner.RunAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessRunResult(0, "host", "", TimeSpan.FromMilliseconds(5), false));
        var sandbox = BuildSandbox(stub, containerEnabled: true, hostRunner: hostRunner);

        // isolate defaults to false (git-style caller).
        var result = await sandbox.RunAsync(
            "dotnet build", ValidCwd, TimeSpan.FromSeconds(30), CancellationToken.None);

        result.Stdout.Should().Be("host");
        stub.InvocationCount.Should().Be(0);
    }

    [Fact]
    public async Task Isolated_command_still_enforces_allowlist()
    {
        var stub = StubContainerRuntime.Success();
        var sandbox = BuildSandbox(stub, containerEnabled: true);

        var act = async () => await sandbox.RunAsync(
            "rm -rf /", ValidCwd, TimeSpan.FromSeconds(30), CancellationToken.None, isolate: true);

        // The allowlist is enforced identically under isolation: rm is not allowed (and not a
        // deny rule), so it is a recoverable miss — and crucially it never reaches the container.
        await act.Should().ThrowAsync<CommandNotAllowedException>();
        stub.InvocationCount.Should().Be(0);
    }
}
