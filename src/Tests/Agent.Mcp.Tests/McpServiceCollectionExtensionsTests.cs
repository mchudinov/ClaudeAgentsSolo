using Agent.Mcp;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agent.Mcp.Tests;

public sealed class McpServiceCollectionExtensionsTests
{
    private static IConfiguration ConfigWith(params (string Key, string Value)[] entries)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => (string?)e.Value))
            .Build();

    [Fact]
    public void AddMcpServices_registers_tool_source_and_connector_as_singletons()
    {
        var services = new ServiceCollection();

        services.AddMcpServices(ConfigWith());

        services.Should().ContainSingle(d =>
            d.ServiceType == typeof(IMcpToolSource)
            && d.ImplementationType == typeof(McpToolSource)
            && d.Lifetime == ServiceLifetime.Singleton);

        services.Should().ContainSingle(d =>
            d.ServiceType == typeof(IMcpClientConnector)
            && d.ImplementationType == typeof(StdioMcpClientConnector)
            && d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddMcpServices_binds_McpOptions_from_McpServers_Servers_section()
    {
        var services = new ServiceCollection();
        var config = ConfigWith(
            ("McpServers:Servers:GitHub:Enabled", "true"),
            ("McpServers:Servers:GitHub:Command", "npx"),
            ("McpServers:Servers:GitHub:Arguments:0", "-y"),
            ("McpServers:Servers:GitHub:Arguments:1", "@modelcontextprotocol/server-github"));

        services.AddMcpServices(config);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<McpOptions>>().Value;

        options.Servers.Should().ContainKey("GitHub");
        options.Servers["GitHub"].Enabled.Should().BeTrue();
        options.Servers["GitHub"].Arguments.Should().BeEquivalentTo(
            new[] { "-y", "@modelcontextprotocol/server-github" },
            o => o.WithStrictOrdering());
    }

    [Fact]
    public void AddMcpServices_accepts_a_custom_section_name()
    {
        var services = new ServiceCollection();
        var config = ConfigWith(("Custom:Servers:Foo:Enabled", "true"));

        services.AddMcpServices(config, sectionName: "Custom");

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<McpOptions>>().Value;

        options.Servers.Should().ContainKey("Foo");
        options.Servers["Foo"].Enabled.Should().BeTrue();
    }
}
