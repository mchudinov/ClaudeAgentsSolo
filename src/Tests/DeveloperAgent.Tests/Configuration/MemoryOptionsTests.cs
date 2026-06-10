using DeveloperAgent.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace DeveloperAgent.Tests.Configuration;

public sealed class MemoryOptionsTests
{
    [Fact]
    public void Defaults_enable_memory_with_bounded_windows()
    {
        var options = new MemoryOptions();
        options.Enabled.Should().BeTrue();
        options.MaxRecentTurns.Should().Be(20);
        options.MaxInjectedPerScope.Should().Be(10);
        options.MaxStoredPerScope.Should().Be(50);
    }

    [Fact]
    public void Binding_from_Memory_section_overrides_defaults()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Memory:Enabled"] = "false",
                ["Memory:MaxRecentTurns"] = "8",
                ["Memory:MaxInjectedPerScope"] = "4",
                ["Memory:MaxStoredPerScope"] = "30",
            })
            .Build();

        var options = new MemoryOptions();
        config.GetSection("Memory").Bind(options);

        options.Enabled.Should().BeFalse();
        options.MaxRecentTurns.Should().Be(8);
        options.MaxInjectedPerScope.Should().Be(4);
        options.MaxStoredPerScope.Should().Be(30);
    }
}
