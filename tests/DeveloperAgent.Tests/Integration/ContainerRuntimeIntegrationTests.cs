using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeveloperAgent.Tests.Integration;

/// <summary>
/// Linux-only, env-gated integration test for <see cref="DockerContainerRuntime"/>.
/// Runs <c>echo</c> inside a real container via the host <c>docker</c> CLI.
/// <para>
/// Requires a Linux host with a working container runtime and the env var
/// <c>CONTAINER_INTEGRATION_IMAGE</c> set to a small pullable image (e.g.
/// <c>busybox:latest</c> or <c>alpine:latest</c>). Excluded from the default unit-test
/// run by the <c>Category=Integration</c> trait and skipped on non-Linux hosts.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class ContainerRuntimeIntegrationTests
{
    private const string EnvImage = "CONTAINER_INTEGRATION_IMAGE";

    [SkippableFact]
    public async Task Echo_runs_inside_a_real_container()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Container isolation integration is Linux-only.");
        var reason = EnvironmentSkip.ReasonIfMissing(EnvImage);
        Skip.If(reason is not null, reason ?? string.Empty);

        var image = Environment.GetEnvironmentVariable(EnvImage)!;

        var tempDir = Path.Combine(Path.GetTempPath(), "container-int-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            var options = Options.Create(new ContainerRuntimeOptions
            {
                Enabled = true,
                Image = image,
                MountPath = "/workspace",
                Cpus = "1",
                Memory = "256m",
                NetworkMode = "none",
                RuntimeExecutable = "docker",
            });

            // DockerContainerRuntime / DefaultProcessRunner have internal ctors in the
            // Agent.Sandbox library (Step-49), so resolve IContainerRuntime through the public
            // AddSandboxServices DI path.
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<ContainerRuntimeOptions>>(options);
            services.AddSandboxServices();
            var runtime = services.BuildServiceProvider().GetRequiredService<IContainerRuntime>();

            var request = new ContainerRunRequest(
                Executable: "echo",
                Arguments: ["hello-from-container"],
                WorkspacePath: tempDir,
                MountPath: "/workspace",
                WorkingDirectoryInContainer: "/workspace",
                Timeout: TimeSpan.FromMinutes(2));

            var result = await runtime.RunInContainerAsync(request, CancellationToken.None);

            result.TimedOut.Should().BeFalse();
            result.ExitCode.Should().Be(0, because: $"echo should succeed; stderr: {result.Stderr}");
            result.Stdout.Should().Contain("hello-from-container");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
