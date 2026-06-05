using Microsoft.Extensions.Configuration;

namespace Agent.Sandbox.Tests;

/// <summary>
/// Binds the live <c>src/DeveloperAgent/appsettings.json</c> once and exposes the
/// resulting <see cref="SandboxOptions"/> / <see cref="WorkspaceOptions"/>.
/// <para>
/// The sandbox/workspace allow- and deny-lists live ONLY in <c>appsettings.json</c>
/// (the C# records carry no default lists), so tests that need the production rule set
/// read it from the bound configuration here rather than from <c>new SandboxOptions()</c>.
/// Replicated in this library's test project (the original lives in
/// <c>DeveloperAgent.Tests</c>) so the moved sandbox mechanics tests keep asserting the
/// REAL shipped deny/allow values against the REAL CommandSandbox — the coverage that
/// matters most for a security boundary.
/// </para>
/// </summary>
internal static class ProductionSandboxConfig
{
    public static IConfigurationRoot Config { get; }
    public static SandboxOptions Sandbox { get; }
    public static WorkspaceOptions Workspace { get; }

    static ProductionSandboxConfig()
    {
        // Walk up from the test bin directory to the repo, then load the live file.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "DeveloperAgent", "appsettings.json")))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException(
                "Could not locate src/DeveloperAgent/appsettings.json from " + AppContext.BaseDirectory);

        Config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(dir.FullName, "src", "DeveloperAgent", "appsettings.json"), optional: false)
            .Build();

        var sandbox = new SandboxOptions();
        Config.GetSection("Sandbox").Bind(sandbox);
        Sandbox = sandbox;

        var workspace = new WorkspaceOptions();
        Config.GetSection("Workspace").Bind(workspace);
        Workspace = workspace;
    }

    public static IReadOnlyList<CommandDenyRule> DeniedCommands => Sandbox.DeniedCommands;
    public static IReadOnlyList<string> DenyPathPatterns => Sandbox.DenyPathPatterns;
    public static IReadOnlyList<string> AllowedHosts => Sandbox.AllowedHosts;
    public static IReadOnlyList<string> AllowedCommands => Workspace.AllowedCommands;
}
