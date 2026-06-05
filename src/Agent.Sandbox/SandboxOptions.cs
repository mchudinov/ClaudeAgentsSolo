using Agent.Tools;

namespace Agent.Sandbox;

/// <summary>
/// Sandbox configuration. Bound from the <c>Sandbox</c> section of
/// <c>appsettings.json</c>. Covers three orthogonal concerns:
/// path-denial (file tools / cwd), command-denial (the shell-run tool),
/// and outbound-host allowlisting (HTTP egress filter).
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
    /// <remarks>
    /// Defined in <c>appsettings.json</c> (<c>Sandbox:DenyPathPatterns</c>) — the single
    /// source. Empty unless configured; the host's <c>Program.cs</c> requires a non-empty list
    /// at startup so the file sandbox is never silently unguarded.
    /// </remarks>
    public IReadOnlyList<string> DenyPathPatterns { get; init; } = [];

    /// <summary>
    /// Additional regex patterns. A path whose normalised absolute form
    /// matches any of these is denied. Default: empty.
    /// </summary>
    public IReadOnlyList<string> SecretFileRegexes { get; init; } = [];

    /// <summary>
    /// Command deny rules consulted by <see cref="ICommandDenyPolicy"/>
    /// before the <c>WorkspaceOptions.AllowedCommands</c> allowlist.
    /// Deny rules take precedence — a command that hits a deny rule is
    /// rejected even when it would otherwise be allowed.
    /// </summary>
    /// <remarks>
    /// Defined in <c>appsettings.json</c> (<c>Sandbox:DeniedCommands</c>) — the single
    /// source. Empty unless configured; the host's <c>Program.cs</c> requires a non-empty list
    /// at startup so the command sandbox is never silently unguarded.
    /// <para>
    /// Because <see cref="WorkspaceOptions.AllowedCommands"/> allows the
    /// whole <c>dotnet</c> / <c>git</c> / <c>gh</c> families, the shipped rules block the
    /// well-known sandbox-bypass commands that breadth makes reachable: arbitrary network
    /// egress (<c>curl</c>, <c>wget</c>), privilege escalation (<c>chmod +x</c>), force-push
    /// and remote-state rewrites (<c>git push --force</c> / <c>-f</c>), secret / branch-protection
    /// mutation and destructive repo / auth operations through the <c>gh</c> CLI
    /// (<c>gh secret …</c>, <c>gh repo delete</c>, <c>gh auth …</c>), mutating raw API calls
    /// (<c>gh api -X/--method DELETE|PUT|POST</c>), and arbitrary tool acquisition
    /// (<c>dotnet tool install</c>). NOTE: <c>gh api</c> with <c>PATCH</c>/<c>HEAD</c>
    /// and <c>dotnet tool update</c> are NOT yet denied — add rules in <c>appsettings.json</c> if needed.
    /// </para>
    /// </remarks>
    public IReadOnlyList<CommandDenyRule> DeniedCommands { get; init; } = [];

    /// <summary>
    /// Host patterns the <see cref="HostAllowlistHandler"/> permits for
    /// outbound HTTP. Each entry is matched against <see cref="Uri.Host"/>:
    /// <list type="bullet">
    /// <item>A leading <c>*.</c> matches any subdomain — e.g.
    /// <c>*.githubusercontent.com</c> matches
    /// <c>raw.githubusercontent.com</c> but not <c>githubusercontent.com</c>.</item>
    /// <item>An exact host string matches only that host.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Defined in <c>appsettings.json</c> (<c>Sandbox:AllowedHosts</c>) — the single
    /// source. Empty unless configured; the host's <c>Program.cs</c> requires a non-empty list
    /// at startup. The shipped hosts cover the three production providers: Anthropic (model
    /// API), GitHub (REST/GraphQL + raw content), and Context7 (MCP documentation).
    /// </remarks>
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];
}

/// <summary>
/// A single command deny rule. Matching is purely token-based — the rule fires
/// when <see cref="Program"/> equals <c>argv[0]</c> and every entry in
/// <see cref="ArgPatterns"/> appears in order among <c>argv[1..]</c> AND every
/// entry in <see cref="ArgContains"/> is a substring of at least one argument.
/// </summary>
/// <param name="Name">
/// Human-readable identifier used in the <see cref="SandboxViolationException"/>
/// message (e.g. <c>no-wget</c>).
/// </param>
/// <param name="Program">
/// The executable token, matched ordinally against <c>argv[0]</c>.
/// </param>
/// <param name="ArgPatterns">
/// Ordered literal tokens that must all appear in <c>argv[1..]</c>, preserving
/// order but not necessarily contiguous (so <c>git push --force</c> matches
/// <c>git push --force origin main</c>). Empty list means program-only match.
/// </param>
/// <param name="ArgContains">
/// Substrings that must each appear inside at least one argument (any position).
/// Used for patterns like <c>gh api ... branch-protection ...</c> where the path
/// portion of <c>gh api</c> contains a sub-resource name. Empty list disables
/// substring matching.
/// </param>
public sealed record CommandDenyRule(
    string Name,
    string Program,
    IReadOnlyList<string>? ArgPatterns = null,
    IReadOnlyList<string>? ArgContains = null);
