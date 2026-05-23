namespace DeveloperAgent.Sandbox;

/// <summary>
/// Sandbox path-denial configuration. Bound from the <c>Sandbox</c> section of
/// <c>appsettings.json</c>.
/// </summary>
/// <remarks>
/// Workspace-escape is computed by <see cref="IPathDenyPolicy"/> and is NOT
/// listed as a pattern — any absolute path outside the caller-supplied workspace
/// root is rejected unconditionally.
/// </remarks>
public sealed record SandboxOptions
{
    /// <summary>
    /// Glob-style patterns that block reading/writing matching paths.
    /// Supported syntax:
    /// <list type="bullet">
    /// <item><c>~</c> at the start of a pattern expands to the OS user home.</item>
    /// <item><c>**</c> matches any number of path segments.</item>
    /// <item><c>*</c> matches any characters within a single segment.</item>
    /// <item>Patterns without a path separator are matched against the basename
    /// (last path segment) — e.g. <c>.env*</c> matches files whose basename
    /// starts with <c>.env</c>, regardless of directory.</item>
    /// <item>Patterns containing a path separator are matched against the full
    /// normalised absolute path.</item>
    /// </list>
    /// </summary>
    public IReadOnlyList<string> DenyPathPatterns { get; init; } =
    [
        "~/.ssh/**",
        ".env*",
        ".git/config",
    ];

    /// <summary>
    /// Additional regex patterns. A path whose normalised absolute form
    /// matches any of these is denied. Default: empty.
    /// </summary>
    public IReadOnlyList<string> SecretFileRegexes { get; init; } = [];
}
