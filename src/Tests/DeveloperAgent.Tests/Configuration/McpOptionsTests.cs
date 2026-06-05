using Agent.Mcp;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace DeveloperAgent.Tests.Configuration;

/// <summary>
/// Host-side guard that the shipped <c>appsettings.json</c> still carries the canonical
/// LLD MCP server definitions under the <c>McpServers:Servers</c> map. The agent-neutral
/// binding/behaviour of <see cref="McpOptions"/> is covered by <c>Agent.Mcp.Tests</c>;
/// this test pins the developer agent's production configuration specifically.
/// </summary>
public sealed class McpOptionsTests
{
    [Fact]
    public void Production_appsettings_loads_LLD_default_npx_invocations()
    {
        // Pin the canonical LLD package names in appsettings.json so any drift in the
        // production config is caught here. This test reads the file the app ships with.
        var appsettingsPath = ResolveAppsettingsPath();
        File.Exists(appsettingsPath).Should().BeTrue("appsettings.json is expected at " + appsettingsPath);

        var config = new ConfigurationBuilder()
            .AddJsonFile(appsettingsPath, optional: false)
            .Build();

        var options = new McpOptions();
        config.GetSection("McpServers").Bind(options);

        options.Servers.Should().ContainKeys("GitHub", "Context7");

        options.Servers["GitHub"].Command.Should().Be("npx");
        options.Servers["GitHub"].Arguments.Should().BeEquivalentTo(
            new[] { "-y", "@modelcontextprotocol/server-github" },
            o => o.WithStrictOrdering());

        options.Servers["Context7"].Command.Should().Be("npx");
        options.Servers["Context7"].Arguments.Should().BeEquivalentTo(
            new[] { "-y", "@upstash/context7-mcp" },
            o => o.WithStrictOrdering());

        // Operators must opt in per environment — shipped defaults are disabled.
        options.Servers["GitHub"].Enabled.Should().BeFalse();
        options.Servers["Context7"].Enabled.Should().BeFalse();
    }

    private static string ResolveAppsettingsPath()
    {
        // tests/DeveloperAgent.Tests/bin/Debug/net10.0 → repo root → src/DeveloperAgent/appsettings.json
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "DeveloperAgent", "appsettings.json")))
        {
            dir = dir.Parent;
        }
        return dir is null
            ? Path.Combine("src", "DeveloperAgent", "appsettings.json")
            : Path.Combine(dir.FullName, "src", "DeveloperAgent", "appsettings.json");
    }
}
