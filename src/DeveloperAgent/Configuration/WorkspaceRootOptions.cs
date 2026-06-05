namespace DeveloperAgent.Configuration;

/// <summary>
/// The workspace-manager half of the workspace configuration: the on-disk root under which
/// per-task workspace directories are laid out. Carved out of the sandbox's
/// <see cref="global::Agent.Sandbox.WorkspaceOptions"/> in Step-50 so the git/workspace layer
/// depends only on the root path, not on the sandbox-only <c>AllowedCommands</c> allowlist.
/// <para>
/// <see cref="RootPath"/> is shared with <see cref="global::Agent.Sandbox.WorkspaceOptions.RootPath"/>:
/// both records bind from the one <c>Workspace</c> configuration section and resolve to the same
/// value. It is duplicated rather than referenced because <c>Agent.Sandbox</c> (which also reads
/// <c>RootPath</c>) cannot depend on the workspace layer — that is the wrong direction in the
/// <c>Tools → Sandbox → Workspace</c> dependency DAG.
/// </para>
/// </summary>
public sealed record WorkspaceRootOptions
{
    /// <summary>
    /// Absolute root path under which all agent workspace directories are created.
    /// Defaults to a subdirectory of the OS temp directory so a non-root process can
    /// create it without elevated permissions. Override via <c>Workspace:RootPath</c>
    /// in configuration for deployment-specific roots.
    /// </summary>
    public string RootPath { get; init; } = Path.Combine(Path.GetTempPath(), "developer-agent", "workspace");
}
