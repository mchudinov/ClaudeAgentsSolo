namespace DeveloperAgent.Configuration;

/// <summary>
/// Root MCP servers configuration. Bound from the <c>McpServers</c> section of <c>appsettings.json</c>
/// per the LLD (<c>docs/low_level_design_software.md</c>).
/// </summary>
/// <remarks>
/// Both servers are <see cref="McpServerOptions.Enabled"/> = <c>false</c> by default so the agent
/// boots cleanly without external NPM tooling. Operators opt in per environment.
/// </remarks>
public sealed record McpOptions
{
    /// <summary>
    /// GitHub MCP — richer repo exploration (multi-file search, semantic issue/PR queries).
    /// LLD-prescribed <c>Command</c>/<c>Arguments</c> defaults live in <c>appsettings.json</c>
    /// (the .NET configuration binder appends to non-empty <see cref="IReadOnlyList{T}"/>
    /// defaults declared in code, which would corrupt operator overrides).
    /// </summary>
    public McpServerOptions GitHub { get; init; } = new();

    /// <summary>
    /// Context7 MCP — live library documentation lookups. See <see cref="GitHub"/> for the
    /// rationale on why command/argument defaults are configured, not coded.
    /// </summary>
    public McpServerOptions Context7 { get; init; } = new();
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
    /// Environment variables to set on the child process. Values may be plain strings
    /// or secret names that <see cref="ISecretResolver"/> can resolve (the resolution
    /// strategy is up to the consuming service).
    /// </summary>
    public IDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();
}
