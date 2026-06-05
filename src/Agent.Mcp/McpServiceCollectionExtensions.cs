using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agent.Mcp;

/// <summary>
/// DI registration for the agent-neutral MCP stdio tool source.
/// </summary>
public static class McpServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="McpOptions"/> from the given configuration section and registers the
    /// stdio connector + tool source as singletons:
    /// <see cref="IMcpClientConnector"/> (<c>StdioMcpClientConnector</c>) and
    /// <see cref="IMcpToolSource"/> (<c>McpToolSource</c>).
    /// </summary>
    /// <remarks>
    /// The connector is registered as a singleton because it owns the launched child processes
    /// for the lifetime of the host and disposes them at shutdown via <see cref="IAsyncDisposable"/>.
    /// Server definitions are read from <c>{sectionName}:Servers</c> (a name-keyed map), so the
    /// library imposes no fixed set of servers.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration to bind <see cref="McpOptions"/> from.</param>
    /// <param name="sectionName">Configuration section holding the <c>Servers</c> map. Defaults to <c>McpServers</c>.</param>
    public static IServiceCollection AddMcpServices(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "McpServers")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrEmpty(sectionName);

        services.AddOptions<McpOptions>().Bind(configuration.GetSection(sectionName));
        services.AddSingleton<IMcpClientConnector, StdioMcpClientConnector>();
        services.AddSingleton<IMcpToolSource, McpToolSource>();

        return services;
    }
}
