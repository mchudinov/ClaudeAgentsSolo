using Agent.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Runtime.Tests;

public sealed class AgentRuntimeServiceCollectionExtensionsTests
{
    [Fact]
    public void AddAgentRuntimeServices_registers_factory_as_singleton()
    {
        var services = new ServiceCollection();

        services.AddAgentRuntimeServices();

        services.Should().ContainSingle(d =>
            d.ServiceType == typeof(IAgentChatClientFactory)
            && d.ImplementationType == typeof(AnthropicChatClientFactory)
            && d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddAgentRuntimeServices_throws_on_null_services()
    {
        var act = () => ((IServiceCollection)null!).AddAgentRuntimeServices();

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddAgentRuntimeServices_registers_the_anthropic_named_http_client()
    {
        var services = new ServiceCollection();

        services.AddAgentRuntimeServices();

        using var provider = services.BuildServiceProvider();
        var httpFactory = provider.GetRequiredService<IHttpClientFactory>();
        httpFactory.CreateClient(AnthropicHttpClients.ChatClient).Should().NotBeNull();
    }

    [Fact]
    public void AddAgentRuntimeServices_invokes_the_configure_callback()
    {
        var services = new ServiceCollection();
        var invoked = false;

        services.AddAgentRuntimeServices(_ => invoked = true);

        invoked.Should().BeTrue("the host composes egress + resilience onto the named client via the callback");
    }

    [Fact]
    public void Factory_resolves_end_to_end_when_a_key_provider_is_registered()
    {
        var services = new ServiceCollection();
        var keyProvider = Substitute.For<IAnthropicApiKeyProvider>();
        keyProvider.GetApiKey().Returns("sk-test");
        services.AddSingleton(keyProvider);

        services.AddAgentRuntimeServices();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IAgentChatClientFactory>().Should().BeOfType<AnthropicChatClientFactory>();
    }
}
