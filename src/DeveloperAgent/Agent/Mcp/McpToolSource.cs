using DeveloperAgent.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Agent.Mcp;

/// <summary>
/// Default <see cref="IMcpToolSource"/>. Walks <see cref="McpOptions"/>, asks the injected
/// <see cref="IMcpClientConnector"/> to connect to each enabled server, and returns the
/// flattened tool set.
/// </summary>
/// <remarks>
/// <para>
/// Results are cached after the first successful call so repeated <see cref="GetToolsAsync"/>
/// invocations across multiple agent runs do not re-spawn child processes. A
/// <see cref="SemaphoreSlim"/> serialises concurrent first-call attempts.
/// </para>
/// <para>
/// A failure connecting to one server is logged but does not abort the whole call — the
/// other server still contributes whatever tools it lists. MCP is an enrichment, not a
/// hard dependency: the agent must remain functional with everything disabled.
/// </para>
/// </remarks>
internal sealed class McpToolSource : IMcpToolSource
{
    private readonly IMcpClientConnector _connector;
    private readonly McpOptions _options;
    private readonly ILogger<McpToolSource> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<AITool>? _cached;

    public McpToolSource(
        IMcpClientConnector connector,
        IOptions<McpOptions> options,
        ILogger<McpToolSource> logger)
    {
        _connector = connector;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken ct)
    {
        if (_cached is { } cached)
            return cached;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is { } cached2)
                return cached2;

            var tools = new List<AITool>();
            await AppendAsync("GitHub", _options.GitHub, tools, ct).ConfigureAwait(false);
            await AppendAsync("Context7", _options.Context7, tools, ct).ConfigureAwait(false);

            _cached = tools;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task AppendAsync(
        string serverName,
        McpServerOptions server,
        List<AITool> sink,
        CancellationToken ct)
    {
        if (!server.Enabled)
        {
            _logger.LogDebug("MCP server {Server} is disabled — skipping.", serverName);
            return;
        }

        try
        {
            var tools = await _connector
                .ConnectAndListToolsAsync(serverName, server, ct)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "MCP server {Server} contributed {Count} tool(s).", serverName, tools.Count);
            sink.AddRange(tools);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // MCP is best-effort enrichment. Failure on one server must not silently fail
            // the whole agent — log loudly and continue.
            _logger.LogWarning(ex,
                "MCP server {Server} failed to start or list tools — continuing without it.",
                serverName);
        }
    }
}
