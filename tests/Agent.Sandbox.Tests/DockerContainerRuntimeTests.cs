using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agent.Sandbox.Tests;

/// <summary>
/// Unit tests for <see cref="DockerContainerRuntime"/>. The load-bearing logic is the
/// <c>docker run</c> argument list; it is asserted directly via the internal arg-builder
/// and via a fake <see cref="IProcessRunner"/> capturing the spawned argv. No real
/// container runtime is required.
/// </summary>
public sealed class DockerContainerRuntimeTests
{
    private static readonly ContainerRunRequest Request = new(
        Executable: "dotnet",
        Arguments: ["build", "--no-restore"],
        WorkspacePath: "/host/workspace",
        MountPath: "/workspace",
        WorkingDirectoryInContainer: "/workspace/repo",
        Timeout: TimeSpan.FromSeconds(30));

    private static ContainerRuntimeOptions DefaultOptions() => new()
    {
        Enabled = true,
        Image = "mcr.microsoft.com/dotnet/sdk:10.0",
        MountPath = "/workspace",
        Cpus = "2",
        Memory = "2g",
        NetworkMode = "none",
        RuntimeExecutable = "docker",
    };

    [Fact]
    public void BuildDockerArguments_isolates_with_readonly_root_and_rw_workspace_mount()
    {
        var args = DockerContainerRuntime.BuildDockerArguments(DefaultOptions(), Request);

        args.Should().StartWith("run");
        args.Should().Contain("--rm");
        args.Should().Contain("--read-only");
        // Workspace bind-mounted read-write.
        args.Should().ContainInConsecutiveOrder("-v", "/host/workspace:/workspace:rw");
    }

    [Fact]
    public void BuildDockerArguments_applies_resource_caps_and_network_policy()
    {
        var args = DockerContainerRuntime.BuildDockerArguments(DefaultOptions(), Request);

        args.Should().ContainInConsecutiveOrder("--cpus", "2");
        args.Should().ContainInConsecutiveOrder("--memory", "2g");
        args.Should().ContainInConsecutiveOrder("--network", "none");
    }

    [Fact]
    public void BuildDockerArguments_places_image_and_command_after_flags()
    {
        var args = DockerContainerRuntime.BuildDockerArguments(DefaultOptions(), Request).ToList();

        var imageIdx = args.IndexOf("mcr.microsoft.com/dotnet/sdk:10.0");
        var execIdx = args.IndexOf("dotnet");

        imageIdx.Should().BeGreaterThan(0);
        execIdx.Should().Be(imageIdx + 1, because: "the command runs as the container entrypoint after the image");
        args.Should().EndWith("--no-restore");
        // Working directory inside the container.
        args.Should().ContainInConsecutiveOrder("-w", "/workspace/repo");
    }

    [Fact]
    public void BuildDockerArguments_omits_empty_caps()
    {
        var opts = DefaultOptions() with { Cpus = "", Memory = "", NetworkMode = "" };

        var args = DockerContainerRuntime.BuildDockerArguments(opts, Request);

        args.Should().NotContain("--cpus");
        args.Should().NotContain("--memory");
        args.Should().NotContain("--network");
    }

    [Fact]
    public async Task RunInContainerAsync_invokes_runtime_executable_with_built_args()
    {
        var runner = Substitute.For<IProcessRunner>();
        IReadOnlyList<string>? capturedArgs = null;
        string? capturedExe = null;
        runner.RunAsync(
            Arg.Do<string>(e => capturedExe = e),
            Arg.Do<IReadOnlyList<string>>(a => capturedArgs = a),
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessRunResult(0, "done", "", TimeSpan.FromMilliseconds(10), false));

        var runtime = new DockerContainerRuntime(
            runner, Options.Create(DefaultOptions()), NullLogger<DockerContainerRuntime>.Instance);

        var result = await runtime.RunInContainerAsync(Request, CancellationToken.None);

        capturedExe.Should().Be("docker");
        capturedArgs.Should().NotBeNull();
        capturedArgs!.Should().StartWith("run");
        result.ExitCode.Should().Be(0);
        result.Stdout.Should().Be("done");
    }

    [Fact]
    public async Task RunInContainerAsync_maps_timeout_and_exit_code_from_process_result()
    {
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessRunResult(-1, "", "killed", TimeSpan.FromSeconds(30), true));

        var runtime = new DockerContainerRuntime(
            runner, Options.Create(DefaultOptions()), NullLogger<DockerContainerRuntime>.Instance);

        var result = await runtime.RunInContainerAsync(Request, CancellationToken.None);

        result.TimedOut.Should().BeTrue();
        result.ExitCode.Should().Be(-1);
        result.Stderr.Should().Be("killed");
    }
}
