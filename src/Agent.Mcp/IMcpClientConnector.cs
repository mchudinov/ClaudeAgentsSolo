using Microsoft.Extensions.AI;

namespace Agent.Mcp;

/// <summary>
/// Single-server seam used by <see cref="McpToolSource"/>. Hides the concrete
/// <c>ModelContextProtocol</c> SDK call site so unit tests can drive
/// <see cref="McpToolSource"/> without spawning a child process.
/// </summary>
internal interface IMcpClientConnector
{
    /// <summary>
    /// Connects to the server described by <paramref name="server"/>, lists its tools, and
    /// returns them. The returned tools are owned by the caller; the implementation must
    /// ensure that any disposable resources (process handles, transports) are tracked and
    /// disposed by the caller via <see cref="IAsyncDisposable"/> on the owning service.
    /// </summary>
    /// <param name="serverName">Diagnostic label and stdio transport name (the map key).</param>
    /// <param name="server">Server-specific options bound from <c>McpServers:Servers:{serverName}</c>.</param>
    /// <param name="ct">Cancellation token observed during connect + list.</param>
    Task<IReadOnlyList<AITool>> ConnectAndListToolsAsync(
        string serverName,
        McpServerOptions server,
        CancellationToken ct);
}
