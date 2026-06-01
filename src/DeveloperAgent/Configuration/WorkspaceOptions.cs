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
    /// The sandbox enforces this list; commands not starting with an entry here are rejected.
    /// </summary>
    public IReadOnlyList<string> AllowedCommands { get; init; } =
    [
        "dotnet restore",
        "dotnet build",
        "dotnet test",
        "git clone",
        "git symbolic-ref",
        "git status",
        "git diff",
        "git checkout",
        "git add",
        "git commit",
        "git push",
    ];
}
