using Dapr.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Memory.Tests;

/// <summary>
/// Verifies <see cref="AgentMemoryServiceCollectionExtensions.AddAgentMemoryServices"/> registers
/// exactly the three Dapr-backed memory services to their expected concrete implementations as
/// singletons — the equivalence guard for the <c>Program.cs</c> registration that this extension
/// replaced (Step-40). The MAF providers and the summarizer/extractor seams are deliberately not
/// registered here, so they are intentionally absent from these assertions.
/// </summary>
public sealed class AgentMemoryServiceCollectionExtensionsTests
{
    private static ServiceProvider BuildProvider(string agentId = "test-agent")
    {
        var services = new ServiceCollection();
        // DaprClientStateAdapter depends on a DaprClient — the host registers it, mirrored here.
        // Building the client constructs the object only; it never connects without a real call.
        services.AddSingleton<DaprClient>(_ => new DaprClientBuilder().Build());
        services.AddAgentMemoryServices(agentId);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Registers_IDaprStateClient_as_DaprClientStateAdapter()
    {
        using var sp = BuildProvider();
        sp.GetRequiredService<IDaprStateClient>().Should().BeOfType<DaprClientStateAdapter>();
    }

    [Fact]
    public void Registers_IAgentSessionStore_as_DaprAgentSessionStore()
    {
        using var sp = BuildProvider();
        sp.GetRequiredService<IAgentSessionStore>().Should().BeOfType<DaprAgentSessionStore>();
    }

    [Fact]
    public void Registers_IAgentMemoryStore_as_DaprAgentMemoryStore()
    {
        using var sp = BuildProvider();
        sp.GetRequiredService<IAgentMemoryStore>().Should().BeOfType<DaprAgentMemoryStore>();
    }

    [Fact]
    public void Registrations_are_singletons()
    {
        using var sp = BuildProvider();
        sp.GetRequiredService<IAgentMemoryStore>().Should().BeSameAs(sp.GetRequiredService<IAgentMemoryStore>());
        sp.GetRequiredService<IAgentSessionStore>().Should().BeSameAs(sp.GetRequiredService<IAgentSessionStore>());
        sp.GetRequiredService<IDaprStateClient>().Should().BeSameAs(sp.GetRequiredService<IDaprStateClient>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Throws_when_agentId_is_null_or_empty(string? agentId)
    {
        var act = () => new ServiceCollection().AddAgentMemoryServices(agentId!);
        act.Should().Throw<ArgumentException>();
    }
}
