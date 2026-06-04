namespace DeveloperAgent.Configuration;

/// <summary>Local workspace sandbox configuration.</summary>
public sealed record WorkspaceOptions
{
    /// <summary>
    /// Absolute root path under which all agent workspace directories are created.
    /// Defaults to a subdirectory of the OS temp directory so a non-root process can
    /// create it without elevated permissions. (The previous "/workspace" default was a
    /// filesystem-root path that a normal host user cannot create — that is the in-container
    /// mount path, see <see cref="ContainerRuntimeOptions.MountPath"/>, not a host path.)
    /// Override via <c>Workspace:RootPath</c> in configuration for deployment-specific roots.
    /// </summary>
    public string RootPath { get; init; } = Path.Combine(Path.GetTempPath(), "developer-agent", "workspace");

    /// <summary>
    /// Command prefixes the agent (and orchestrator) are permitted to run.
    /// The sandbox enforces this list as a PREFIX allowlist
    /// (<see cref="Workspace.CommandSandbox"/>): a bare entry such as <c>"dotnet"</c>
    /// allows every <c>dotnet …</c> invocation. We deliberately allow the whole
    /// <c>dotnet</c>, <c>git</c> and <c>gh</c> families plus the read-only file
    /// commands <c>ls</c> / <c>dir</c> and <c>pwd</c>.
    /// <para>
    /// Breadth here is safe because the command DENY policy
    /// (<see cref="Sandbox.SandboxOptions.DeniedCommands"/>) runs BEFORE this allowlist
    /// and blocks the dangerous verbs reachable inside those families —
    /// <c>git push --force</c>, <c>gh repo delete</c>, <c>gh auth …</c>,
    /// <c>gh api -X/--method DELETE|PUT|POST</c>, <c>gh secret …</c>,
    /// <c>dotnet tool install</c>, plus <c>curl</c>/<c>wget</c>/<c>chmod +x</c>.
    /// </para>
    /// <para>
    /// <c>cd</c> is intentionally NOT allowed: the sandbox never invokes a real shell,
    /// so a <c>cd</c> segment would run as a direct child-process exec and could not
    /// change the agent's working directory. Callers set the working directory via the
    /// <c>workingDirectory</c> argument instead.
    /// </para>
    /// <para>
    /// Defined in <c>appsettings.json</c> (<c>Workspace:AllowedCommands</c>) — the single
    /// source. Empty unless configured; <c>Program.cs</c> requires a non-empty list at
    /// startup. The shipped list is <c>dotnet</c> / <c>git</c> / <c>gh</c> plus the
    /// read-only commands <c>ls</c> / <c>dir</c> / <c>pwd</c> / <c>cat</c>.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> AllowedCommands { get; init; } = [];
}
