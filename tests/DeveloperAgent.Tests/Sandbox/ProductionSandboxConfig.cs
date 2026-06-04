using DeveloperAgent.Configuration;
using DeveloperAgent.Sandbox;
using Microsoft.Extensions.Configuration;

namespace DeveloperAgent.Tests.Sandbox;

/// <summary>
/// Binds the live <c>src/DeveloperAgent/appsettings.json</c> once and exposes the
/// resulting <see cref="SandboxOptions"/> / <see cref="WorkspaceOptions"/>.
/// <para>
/// After Step-41 the sandbox/workspace allow- and deny-lists live ONLY in
/// <c>appsettings.json</c> (the C# records no longer seed default lists, so the
/// <see cref="Microsoft.Extensions.Configuration.ConfigurationBinder"/> can no
/// longer append config entries onto in-code defaults and double them). Tests that
/// need the production rule set therefore read it from the bound configuration here
/// instead of from <c>new SandboxOptions()</c>.
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
