using DeveloperAgent.Configuration;
using Microsoft.Extensions.Configuration;

namespace DeveloperAgent.Tests.Sandbox;

/// <summary>
/// Regression for Step-41. The sandbox/workspace lists are defined in
/// <c>appsettings.json</c>, and the <see cref="ConfigurationBinder"/> APPENDS bound
/// array entries onto any pre-seeded default collection — even an
/// <see cref="IReadOnlyList{T}"/> (it seeds a fresh list from the existing instance,
/// then adds the config children). With a non-empty C# default that restates the
/// appsettings entries, every list loaded twice (e.g. 38 deny rules instead of 19).
/// Emptying the C# defaults makes <c>appsettings.json</c> the single source: each list
/// now binds to exactly its configured entries, with no duplicates.
/// </summary>
public sealed class SandboxOptionsBindingTests
{
    private static int AppSettingsCount(string section) =>
        ProductionSandboxConfig.Config.GetSection(section).GetChildren().Count();

    [Fact]
    public void DeniedCommands_bind_without_duplication()
    {
        var rules = ProductionSandboxConfig.Sandbox.DeniedCommands;

        rules.Should().HaveCount(AppSettingsCount("Sandbox:DeniedCommands"));
        rules.Select(r => r.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void DenyPathPatterns_bind_without_duplication()
    {
        var patterns = ProductionSandboxConfig.Sandbox.DenyPathPatterns;

        patterns.Should().HaveCount(AppSettingsCount("Sandbox:DenyPathPatterns"));
        patterns.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void AllowedHosts_bind_without_duplication()
    {
        var hosts = ProductionSandboxConfig.Sandbox.AllowedHosts;

        hosts.Should().HaveCount(AppSettingsCount("Sandbox:AllowedHosts"));
        hosts.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void AllowedCommands_bind_without_duplication()
    {
        var commands = ProductionSandboxConfig.Workspace.AllowedCommands;

        commands.Should().HaveCount(AppSettingsCount("Workspace:AllowedCommands"));
        commands.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Default_records_seed_no_lists_so_appsettings_is_the_single_source()
    {
        // Root cause guard: a non-empty C# default + a restated appsettings array makes
        // the binder append. Empty defaults guarantee appsettings binds cleanly (replace,
        // not append) and remain the single source of truth for these security lists.
        new SandboxOptions().DeniedCommands.Should().BeEmpty();
        new SandboxOptions().DenyPathPatterns.Should().BeEmpty();
        new SandboxOptions().AllowedHosts.Should().BeEmpty();
        new WorkspaceOptions().AllowedCommands.Should().BeEmpty();
    }
}
