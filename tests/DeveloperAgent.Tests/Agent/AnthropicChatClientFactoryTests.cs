using DeveloperAgent.Agent;
using DeveloperAgent.Configuration;
using FluentAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace DeveloperAgent.Tests.Agent;

/// <summary>
/// Guards the Anthropic chat-client wiring. The provider is the <c>Anthropic</c> NuGet package,
/// exposed as an <see cref="IChatClient"/> via its
/// <c>Microsoft.Extensions.AI.AnthropicClientExtensions.AsIChatClient</c> extension.
/// </summary>
/// <remarks>
/// Regression for the runtime crash
/// <c>MissingMethodException: WebSearchToolResultContent.get_Results()</c>: the property
/// <c>WebSearchToolResultContent.Results</c> was renamed to <c>Outputs</c> in
/// <c>Microsoft.Extensions.AI.Abstractions</c> 10.5.x. <c>Anthropic</c> 12.11.0 was compiled
/// against the old <c>Results</c> shape, but the rest of the graph (Microsoft.Agents.AI 1.6.2,
/// ModelContextProtocol.Core 1.3.0) forces Abstractions up to 10.5.x where <c>Results</c> no
/// longer exists, so every model call threw. <c>Anthropic</c> 12.18.0 is the first release
/// compiled against Abstractions 10.5.x (it calls <c>Outputs</c>); anything older crashes.
/// </remarks>
public sealed class AnthropicChatClientFactoryTests
{
    // First Anthropic release compiled against Microsoft.Extensions.AI.Abstractions 10.5.x,
    // i.e. the first one that reads WebSearchToolResultContent.Outputs instead of .Results.
    private static readonly Version MinSafeAnthropicVersion = new(12, 18, 0, 0);

    private static AnthropicChatClientFactory BuildFactory()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var secrets = new SecretsBundle(AnthropicApiKey: "sk-test", GitHubToken: "ghs_test");
        return new AnthropicChatClientFactory(secrets, httpClientFactory);
    }

    [Fact]
    public void Create_returns_a_chat_client()
    {
        var factory = BuildFactory();

        var client = factory.Create("claude-opus-4-7");

        client.Should().NotBeNull();
    }

    [Fact]
    public void Anthropic_provider_is_compiled_against_the_renamed_Outputs_api()
    {
        // typeof binds to the same Anthropic package the factory uses at runtime.
        var anthropicAssemblyVersion = typeof(global::Anthropic.AnthropicClient).Assembly.GetName().Version;

        anthropicAssemblyVersion.Should().NotBeNull();
        anthropicAssemblyVersion!.Should().BeGreaterThanOrEqualTo(
            MinSafeAnthropicVersion,
            because: "Anthropic < 12.18.0 calls the removed WebSearchToolResultContent.get_Results(), " +
                     "throwing MissingMethodException against the Abstractions 10.5.x the rest of the graph requires");
    }
}
