using DeveloperAgent.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace DeveloperAgent.Tests.Configuration;

public sealed class McpOptionsTests
{
    [Fact]
    public void Defaults_disable_both_servers()
    {
        var options = new McpOptions();

        options.GitHub.Should().NotBeNull();
        options.Context7.Should().NotBeNull();
        options.GitHub.Enabled.Should().BeFalse("agent must boot cleanly without MCP");
        options.Context7.Enabled.Should().BeFalse("agent must boot cleanly without MCP");
    }

    [Fact]
    public void Defaults_have_empty_arguments_and_env()
    {
        // LLD-prescribed Command/Arguments defaults live in appsettings.json, not in code,
        // because the .NET configuration binder would otherwise APPEND operator-supplied
        // arguments onto the in-code defaults rather than replacing them.
        var options = new McpOptions();

        options.GitHub.Command.Should().Be("npx");
        options.GitHub.Arguments.Should().BeEmpty();
        options.GitHub.Env.Should().BeEmpty();

        options.Context7.Command.Should().Be("npx");
        options.Context7.Arguments.Should().BeEmpty();
        options.Context7.Env.Should().BeEmpty();
    }

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

        options.GitHub.Command.Should().Be("npx");
        options.GitHub.Arguments.Should().BeEquivalentTo(
            new[] { "-y", "@modelcontextprotocol/server-github" },
            o => o.WithStrictOrdering());

        options.Context7.Command.Should().Be("npx");
        options.Context7.Arguments.Should().BeEquivalentTo(
            new[] { "-y", "@upstash/context7-mcp" },
            o => o.WithStrictOrdering());

        // Operators must opt in per environment — shipped defaults are disabled.
        options.GitHub.Enabled.Should().BeFalse();
        options.Context7.Enabled.Should().BeFalse();
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

    [Fact]
    public void Binding_from_configuration_populates_both_servers()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["McpServers:GitHub:Enabled"] = "true",
                ["McpServers:GitHub:Command"] = "node",
                ["McpServers:GitHub:Arguments:0"] = "dist/server-github.js",
                ["McpServers:GitHub:Env:GITHUB_TOKEN"] = "github-token",
                ["McpServers:Context7:Enabled"] = "true",
                ["McpServers:Context7:Command"] = "npx",
                ["McpServers:Context7:Arguments:0"] = "-y",
                ["McpServers:Context7:Arguments:1"] = "@upstash/context7-mcp",
                ["McpServers:Context7:Env:UPSTASH_KEY"] = "upstash-key",
            })
            .Build();

        var options = new McpOptions();
        config.GetSection("McpServers").Bind(options);

        options.GitHub.Enabled.Should().BeTrue();
        options.GitHub.Command.Should().Be("node");
        options.GitHub.Arguments.Should().ContainSingle().Which.Should().Be("dist/server-github.js");
        options.GitHub.Env.Should().ContainKey("GITHUB_TOKEN").WhoseValue.Should().Be("github-token");

        options.Context7.Enabled.Should().BeTrue();
        options.Context7.Command.Should().Be("npx");
        options.Context7.Arguments.Should().BeEquivalentTo(
            new[] { "-y", "@upstash/context7-mcp" },
            o => o.WithStrictOrdering());
        options.Context7.Env.Should().ContainKey("UPSTASH_KEY").WhoseValue.Should().Be("upstash-key");
    }

    [Fact]
    public void Binding_disabled_server_can_omit_command_and_arguments()
    {
        // A server with Enabled=false should still bind cleanly even when callers
        // leave Command/Arguments out — the Command default must take effect.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["McpServers:GitHub:Enabled"] = "false",
            })
            .Build();

        var options = new McpOptions();
        config.GetSection("McpServers").Bind(options);

        options.GitHub.Enabled.Should().BeFalse();
        // Command keeps its default; Arguments is empty by design (see record summary).
        options.GitHub.Command.Should().Be("npx");
        options.GitHub.Arguments.Should().BeEmpty();
    }
}
