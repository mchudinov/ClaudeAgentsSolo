namespace DeveloperAgent.Configuration;

/// <summary>Local workspace sandbox configuration.</summary>
public sealed record WorkspaceOptions
{
    /// <summary>Absolute root path under which all agent workspace directories are created.</summary>
    public string RootPath { get; init; } = "/workspace";

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
