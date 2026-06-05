using Microsoft.Extensions.AI;

namespace Agent.Mcp;

/// <summary>
/// Provides Microsoft Agent Framework <see cref="AITool"/> instances exposed by configured MCP servers.
/// </summary>
/// <remarks>
/// <para>
/// Implementations connect to all <c>Enabled = true</c> servers in <see cref="McpOptions"/>,
/// list their tools, and return the flattened set. Disabled or unavailable servers contribute
/// zero tools without throwing — MCP is an enrichment, not a hard dependency.
/// </para>
/// <para>
/// The result is intended to be concatenated with whatever local tool list the consuming agent
/// already provides.
/// </para>
/// </remarks>
public interface IMcpToolSource
{
    /// <summary>
    /// Returns the union of tools published by all enabled MCP servers.
    /// </summary>
    /// <param name="ct">Cancellation token observed while connecting and listing.</param>
    Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken ct);
}
