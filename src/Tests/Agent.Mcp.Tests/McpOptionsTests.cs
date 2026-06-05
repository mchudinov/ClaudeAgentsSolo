using Agent.Mcp;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Agent.Mcp.Tests;

public sealed class McpOptionsTests
{
    [Fact]
    public void Default_McpOptions_has_no_servers()
    {
        new McpOptions().Servers.Should().BeEmpty("an agent boots cleanly without any MCP servers");
    }

    [Fact]
    public void Default_McpServerOptions_is_disabled_with_npx_and_empty_collections()
    {
        // LLD-prescribed Command/Arguments defaults live in configuration, not in code,
        // because the .NET configuration binder would otherwise APPEND operator-supplied
        // arguments onto the in-code defaults rather than replacing them.
        var server = new McpServerOptions();

        server.Enabled.Should().BeFalse("servers must be opt-in per environment");
        server.Command.Should().Be("npx");
        server.Arguments.Should().BeEmpty();
        server.Env.Should().BeEmpty();
    }

    [Fact]
    public void Binding_from_configuration_populates_servers_by_name()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["McpServers:Servers:GitHub:Enabled"] = "true",
                ["McpServers:Servers:GitHub:Command"] = "node",
                ["McpServers:Servers:GitHub:Arguments:0"] = "dist/server-github.js",
                ["McpServers:Servers:GitHub:Env:GITHUB_TOKEN"] = "github-token",
                ["McpServers:Servers:Context7:Enabled"] = "true",
                ["McpServers:Servers:Context7:Command"] = "npx",
                ["McpServers:Servers:Context7:Arguments:0"] = "-y",
                ["McpServers:Servers:Context7:Arguments:1"] = "@upstash/context7-mcp",
                ["McpServers:Servers:Context7:Env:UPSTASH_KEY"] = "upstash-key",
            })
            .Build();

        var options = new McpOptions();
        config.GetSection("McpServers").Bind(options);

        options.Servers.Should().ContainKeys("GitHub", "Context7");

        var github = options.Servers["GitHub"];
        github.Enabled.Should().BeTrue();
        github.Command.Should().Be("node");
        github.Arguments.Should().ContainSingle().Which.Should().Be("dist/server-github.js");
        github.Env.Should().ContainKey("GITHUB_TOKEN").WhoseValue.Should().Be("github-token");

        var context7 = options.Servers["Context7"];
        context7.Enabled.Should().BeTrue();
        context7.Command.Should().Be("npx");
        context7.Arguments.Should().BeEquivalentTo(
            new[] { "-y", "@upstash/context7-mcp" },
            o => o.WithStrictOrdering());
        context7.Env.Should().ContainKey("UPSTASH_KEY").WhoseValue.Should().Be("upstash-key");
    }

    [Fact]
    public void Binding_disabled_server_can_omit_command_and_arguments()
    {
        // A server with Enabled=false should still bind cleanly even when callers
        // leave Command/Arguments out — the Command default must take effect.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["McpServers:Servers:GitHub:Enabled"] = "false",
            })
            .Build();

        var options = new McpOptions();
        config.GetSection("McpServers").Bind(options);

        var github = options.Servers["GitHub"];
        github.Enabled.Should().BeFalse();
        // Command keeps its default; Arguments is empty by design (see record summary).
        github.Command.Should().Be("npx");
        github.Arguments.Should().BeEmpty();
    }
}
