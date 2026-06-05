namespace Agent.Mcp;

/// <summary>
/// Root MCP servers configuration. Bound from the <c>McpServers</c> section of the host's
/// configuration. The set of servers is an open, name-keyed map — the library carries no
/// opinion about which servers exist, so any agent can declare its own.
/// </summary>
/// <remarks>
/// Servers are <see cref="McpServerOptions.Enabled"/> = <c>false</c> by default so an agent
/// boots cleanly without external tooling; operators opt in per environment.
/// </remarks>
public sealed record McpOptions
{
    /// <summary>
    /// Configured MCP servers keyed by server name (the name is used as the diagnostic label
    /// and the stdio transport name). Bound from <c>McpServers:Servers</c>. The per-server
    /// <c>Command</c>/<c>Arguments</c> defaults live in the host's configuration (the .NET
    /// configuration binder appends to non-empty <see cref="IReadOnlyList{T}"/> defaults
    /// declared in code, which would corrupt operator overrides).
    /// </summary>
    public IDictionary<string, McpServerOptions> Servers { get; init; }
        = new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Per-MCP-server stdio transport configuration.</summary>
public sealed record McpServerOptions
{
    /// <summary>
    /// When <c>false</c>, the server is skipped at startup and contributes no tools. Default <c>false</c>.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>Executable used to launch the server (typically <c>npx</c>).</summary>
    public string Command { get; init; } = "npx";

    /// <summary>
    /// Command-line arguments passed to <see cref="Command"/>. Typed as
    /// <see cref="IReadOnlyList{T}"/> so the .NET configuration binder replaces the default
    /// list when callers specify <c>Arguments</c> rather than appending to it (the binder
    /// only mutates collections it can write to via <c>Add</c>).
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Environment variables to set on the child process. Values may be plain strings or
    /// secret names the host resolves before binding (the resolution strategy is up to the
    /// consuming service).
    /// </summary>
    public IDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();
}
