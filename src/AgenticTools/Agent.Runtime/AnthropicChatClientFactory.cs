using Anthropic;
using Microsoft.Extensions.AI;

namespace Agent.Runtime;

/// <summary>
/// Production <see cref="IAgentChatClientFactory"/> backed by the <c>Anthropic</c> NuGet package,
/// exposed as an <see cref="IChatClient"/> via that package's <c>AnthropicClientExtensions.AsIChatClient</c>
/// extension. (MAF agent-abstraction wrapping lives in the host; this library returns a raw chat client.)
/// </summary>
/// <remarks>
/// The underlying <see cref="HttpClient"/> is obtained from <see cref="IHttpClientFactory"/>
/// under the <see cref="AnthropicHttpClients.ChatClient"/> name so whatever resilience pipeline
/// (timeouts + retries with exponential back-off + circuit breaker) and egress filter the host
/// composed onto that named client via <see cref="AgentRuntimeServiceCollectionExtensions.AddAgentRuntimeServices"/>
/// is in effect on every Anthropic request. The API key is read lazily on first
/// <see cref="Create"/> (not in the ctor) so an unconfigured process still boots.
/// </remarks>
public sealed class AnthropicChatClientFactory : IAgentChatClientFactory
{
    private const int DefaultMaxTokens = 32_000;

    private readonly IAnthropicApiKeyProvider _apiKeyProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private AnthropicClient? _client;

    public AnthropicChatClientFactory(IAnthropicApiKeyProvider apiKeyProvider, IHttpClientFactory httpClientFactory)
    {
        _apiKeyProvider = apiKeyProvider;
        _httpClientFactory = httpClientFactory;
    }

    public IChatClient Create(string modelId)
    {
        // The IHttpClientFactory owns the lifetime of the returned HttpClient (it pools
        // the inner HttpMessageHandler). We construct AnthropicClient with this HttpClient
        // and do NOT dispose the SDK client — disposing it would dispose the pooled
        // HttpClient and tear down resilience state. As a singleton this factory lives
        // for the process lifetime, which is correct.
        if (_client is null)
        {
            var http = _httpClientFactory.CreateClient(AnthropicHttpClients.ChatClient);
            _client = new AnthropicClient
            {
                ApiKey = _apiKeyProvider.GetApiKey(),
                HttpClient = http,
            };
        }
        return _client.AsIChatClient(modelId, DefaultMaxTokens);
    }
}
