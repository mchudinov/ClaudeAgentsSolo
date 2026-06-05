using Microsoft.Extensions.DependencyInjection;

namespace Agent.Workspace;

/// <summary>
/// DI registration for the agent-neutral git client + workspace manager.
/// </summary>
public static class WorkspaceServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IGitClient"/> (<c>GitClient</c>) and <see cref="IWorkspaceManager"/>
    /// (<c>WorkspaceManager</c>) as singletons. Both have public constructors, so no internal-ctor
    /// factory wiring is needed (unlike <c>AddSandboxServices</c>).
    /// <para>
    /// The host must additionally: register an <see cref="IGitTokenProvider"/>; register an
    /// <c>ICommandSandbox</c> (via <c>Agent.Sandbox.AddSandboxServices</c>); and bind
    /// <see cref="DiffScopeLimitOptions"/> + <see cref="WorkspaceRootOptions"/> from configuration.
    /// </para>
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddWorkspaceGitServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IGitClient, GitClient>();
        services.AddSingleton<IWorkspaceManager, WorkspaceManager>();

        return services;
    }
}
