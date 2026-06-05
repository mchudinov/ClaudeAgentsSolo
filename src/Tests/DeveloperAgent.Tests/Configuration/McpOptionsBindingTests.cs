using Agent.Mcp;
using DeveloperAgent.Tests.Sandbox;
using Microsoft.Extensions.Configuration;

namespace DeveloperAgent.Tests.Configuration;

/// <summary>
/// Step-41 follow-up audit. The sandbox/workspace lists were not the only appsettings
/// collections that bind to an options record — <c>McpServers:Servers:GitHub:Arguments</c>
/// and <c>McpServers:Servers:Context7:Arguments</c> (plus their <c>Env</c> maps) do too.
/// These tests prove those do NOT suffer the binder-append duplication (38-instead-of-19)
/// that hit the sandbox lists: <see cref="McpServerOptions.Arguments"/> defaults to
/// <see cref="Array.Empty{T}"/>, so the <see cref="ConfigurationBinder"/> has an empty
/// default to append onto and each list binds to exactly its configured entries.
/// </summary>
public sealed class McpOptionsBindingTests
{
    private static McpServerOptions Bind(string section)
    {
        var options = new McpServerOptions();
        ProductionSandboxConfig.Config.GetSection(section).Bind(options);
        return options;
    }

    private static int AppSettingsCount(string section) =>
        ProductionSandboxConfig.Config.GetSection(section).GetChildren().Count();

    [Fact]
    public void GitHub_Arguments_bind_without_duplication()
    {
        var github = Bind("McpServers:Servers:GitHub");

        github.Arguments.Should().HaveCount(AppSettingsCount("McpServers:Servers:GitHub:Arguments"));
        github.Arguments.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Context7_Arguments_bind_without_duplication()
    {
        var context7 = Bind("McpServers:Servers:Context7");

        context7.Arguments.Should().HaveCount(AppSettingsCount("McpServers:Servers:Context7:Arguments"));
        context7.Arguments.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Default_McpServerOptions_seeds_empty_collections_so_appsettings_is_the_single_source()
    {
        // Root-cause guard: an empty in-code default leaves the binder nothing to append
        // onto, so appsettings replaces (not doubles) Arguments/Env.
        new McpServerOptions().Arguments.Should().BeEmpty();
        new McpServerOptions().Env.Should().BeEmpty();
    }
}
