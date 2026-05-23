using DeveloperAgent.Configuration;
using Microsoft.Extensions.AI;

namespace DeveloperAgent.Agent.Mcp;

/// <summary>
/// Single-server seam used by <see cref="McpToolSource"/>. Hides the concrete
/// <c>ModelContextProtocol</c> SDK call site so unit tests can drive
/// <see cref="McpToolSource"/> without spawning <c>npx</c>.
/// </summary>
internal interface IMcpClientConnector
{
    /// <summary>
    /// Connects to the server described by <paramref name="server"/>, lists its tools, and
    /// returns them. The returned tools are owned by the caller; the implementation must
    /// ensure that any disposable resources (process handles, transports) are tracked and
    /// disposed by the caller via <see cref="IAsyncDisposable"/> on the owning service.
    /// </summary>
    /// <param name="serverName">Diagnostic label (e.g. <c>"GitHub"</c>, <c>"Context7"</c>).</param>
    /// <param name="server">Server-specific options bound from <c>McpServers:{serverName}</c>.</param>
    /// <param name="ct">Cancellation token observed during connect + list.</param>
    Task<IReadOnlyList<AITool>> ConnectAndListToolsAsync(
        string serverName,
        McpServerOptions server,
        CancellationToken ct);
}
