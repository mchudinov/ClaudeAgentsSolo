using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Agent.Mcp;

/// <summary>
/// Production <see cref="IMcpClientConnector"/> that launches MCP servers as child processes
/// over the stdio transport using the official <c>ModelContextProtocol</c> NuGet package
/// (<c>McpClient.CreateAsync</c> + <see cref="StdioClientTransport"/>).
/// </summary>
/// <remarks>
/// The connector owns each launched <see cref="McpClient"/> and disposes them on
/// <see cref="DisposeAsync"/>, which is invoked by the DI container at host shutdown
/// (the service is registered as a singleton with <see cref="IAsyncDisposable"/>).
/// </remarks>
internal sealed class StdioMcpClientConnector : IMcpClientConnector, IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<StdioMcpClientConnector> _logger;
    private readonly List<McpClient> _clients = new();
    private readonly object _clientsLock = new();
    private int _disposed;

    public StdioMcpClientConnector(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<StdioMcpClientConnector>();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AITool>> ConnectAndListToolsAsync(
        string serverName,
        McpServerOptions server,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var transportOptions = new StdioClientTransportOptions
        {
            Name = serverName,
            Command = server.Command,
            Arguments = server.Arguments?.ToList() ?? new List<string>(),
        };

        if (server.Env is { Count: > 0 })
        {
            transportOptions.EnvironmentVariables = server
                .Env
                .ToDictionary(kvp => kvp.Key, kvp => (string?)kvp.Value);
        }

        var transport = new StdioClientTransport(transportOptions, _loggerFactory);

        _logger.LogInformation(
            "Starting MCP server {Server}: {Command} {Args}",
            serverName, server.Command, string.Join(' ', transportOptions.Arguments));

        var client = await McpClient
            .CreateAsync(transport, clientOptions: null, _loggerFactory, ct)
            .ConfigureAwait(false);

        lock (_clientsLock)
        {
            _clients.Add(client);
        }

        var tools = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
        // McpClientTool extends Microsoft.Extensions.AI.AIFunction (which extends AITool),
        // so the cast is safe and no adapter is needed.
        return tools.Cast<AITool>().ToList();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        McpClient[] snapshot;
        lock (_clientsLock)
        {
            snapshot = _clients.ToArray();
            _clients.Clear();
        }

        foreach (var client in snapshot)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing MCP client.");
            }
        }
    }
}
