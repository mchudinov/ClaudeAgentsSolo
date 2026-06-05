using Agent.Runtime;
using Microsoft.Extensions.AI;

namespace Agent.Runtime.Tests;

/// <summary>
/// Guards the Anthropic chat-client wiring. The provider is the <c>Anthropic</c> NuGet package,
/// exposed as an <see cref="IChatClient"/> via <c>Microsoft.Agents.AI.Anthropic</c>'s
/// <c>AsIChatClient</c> extension.
/// </summary>
/// <remarks>
/// Regression for the runtime crash <c>MissingMethodException: WebSearchToolResultContent.get_Results()</c>:
/// the property <c>Results</c> was renamed to <c>Outputs</c> in <c>Microsoft.Extensions.AI.Abstractions</c>
/// 10.5.x. <c>Anthropic</c> &lt;= 12.16.0 was compiled against the old <c>Results</c> shape, but the rest of
/// the graph forces Abstractions to 10.5.x where <c>Results</c> no longer exists, so every model call threw.
/// <c>Anthropic</c> 12.18.0 is the first release compiled against Abstractions 10.5.x (it reads <c>Outputs</c>).
/// </remarks>
public sealed class AnthropicChatClientFactoryTests
{
    // First Anthropic release compiled against Microsoft.Extensions.AI.Abstractions 10.5.x.
    private static readonly Version MinSafeAnthropicVersion = new(12, 18, 0, 0);

    private static IHttpClientFactory StubHttpClientFactory()
    {
        var f = Substitute.For<IHttpClientFactory>();
        f.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        return f;
    }

    private static IAnthropicApiKeyProvider KeyProvider(string key = "sk-test")
    {
        var p = Substitute.For<IAnthropicApiKeyProvider>();
        p.GetApiKey().Returns(key);
        return p;
    }

    [Fact]
    public void Create_returns_a_chat_client()
    {
        var factory = new AnthropicChatClientFactory(KeyProvider(), StubHttpClientFactory());

        var client = factory.Create("claude-opus-4-7");

        client.Should().NotBeNull();
    }

    [Fact]
    public void Create_sources_its_HttpClient_from_the_anthropic_named_client()
    {
        var httpClientFactory = StubHttpClientFactory();
        var factory = new AnthropicChatClientFactory(KeyProvider(), httpClientFactory);

        factory.Create("claude-opus-4-7");

        httpClientFactory.Received().CreateClient(AnthropicHttpClients.ChatClient);
    }

    [Fact]
    public void Api_key_is_read_lazily_on_first_Create_not_in_the_ctor()
    {
        // Preserves "an unconfigured process still boots": constructing the factory must not touch
        // the key provider; the key is resolved only when a chat client is first created.
        var keyProvider = KeyProvider();

        var factory = new AnthropicChatClientFactory(keyProvider, StubHttpClientFactory());
        keyProvider.DidNotReceive().GetApiKey();

        factory.Create("claude-opus-4-7");
        keyProvider.Received(1).GetApiKey();
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
