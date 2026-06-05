using Agent.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Sandbox;

/// <summary>
/// DI registration for the agent-neutral command/file/egress sandbox.
/// </summary>
public static class SandboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers the sandbox services as singletons (plus the egress handler as a transient):
    /// <see cref="IProcessRunner"/> (<c>DefaultProcessRunner</c>), <see cref="IPathDenyPolicy"/>
    /// (<c>PathDenyPolicy</c>), <see cref="ICommandDenyPolicy"/> (<c>CommandDenyPolicy</c>),
    /// <see cref="IContainerRuntime"/> (<c>DockerContainerRuntime</c>),
    /// <see cref="ICommandSandbox"/> (<c>CommandSandbox</c>), and
    /// <see cref="HostAllowlistHandler"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="IProcessRunner"/> is an internal abstraction and <c>CommandSandbox</c> /
    /// <c>DockerContainerRuntime</c> have internal constructors, so they are registered via
    /// factory lambdas that execute inside this assembly. The caller (host) is responsible for
    /// binding <see cref="SandboxOptions"/>, <see cref="WorkspaceOptions"/> and
    /// <see cref="ContainerRuntimeOptions"/> from configuration (with whatever fail-closed
    /// <c>ValidateOnStart</c> policy it wants) and for adding a logging provider.
    /// <para>
    /// <see cref="HostAllowlistHandler"/> is registered as a transient so the host can compose it
    /// as the outer message handler on its named HttpClients via
    /// <c>AddHttpMessageHandler&lt;HostAllowlistHandler&gt;()</c>; the egress composition itself
    /// stays host-side (the sandbox owns no named HttpClient).
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddSandboxServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IProcessRunner>(_ => new DefaultProcessRunner());
        services.AddSingleton<IPathDenyPolicy, PathDenyPolicy>();
        services.AddSingleton<ICommandDenyPolicy, CommandDenyPolicy>();

        // DockerContainerRuntime has an internal ctor (depends on the internal IProcessRunner),
        // so register it via a factory lambda — same reason as CommandSandbox below.
        services.AddSingleton<IContainerRuntime>(sp => new DockerContainerRuntime(
            sp.GetRequiredService<IProcessRunner>(),
            sp.GetRequiredService<IOptions<ContainerRuntimeOptions>>(),
            sp.GetRequiredService<ILogger<DockerContainerRuntime>>()));

        services.AddSingleton<ICommandSandbox>(sp => new CommandSandbox(
            sp.GetRequiredService<IProcessRunner>(),
            sp.GetRequiredService<IOptions<WorkspaceOptions>>(),
            sp.GetRequiredService<IPathDenyPolicy>(),
            sp.GetRequiredService<ICommandDenyPolicy>(),
            sp.GetRequiredService<ILogger<CommandSandbox>>(),
            sp.GetRequiredService<IContainerRuntime>(),
            sp.GetRequiredService<IOptions<ContainerRuntimeOptions>>()));

        // Transient so each named HttpClient pipeline that composes it via
        // AddHttpMessageHandler<HostAllowlistHandler>() resolves a fresh instance per request.
        services.AddTransient<HostAllowlistHandler>();

        return services;
    }
}
