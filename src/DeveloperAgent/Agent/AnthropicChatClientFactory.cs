using Anthropic;
using DeveloperAgent.Configuration;
using DeveloperAgent.Resilience;
using Microsoft.Extensions.AI;

namespace DeveloperAgent.Agent;

/// <summary>
/// Production <see cref="IAgentChatClientFactory"/> backed by the official Anthropic .NET SDK
/// (the <c>Anthropic</c> NuGet package), exposed as an <see cref="IChatClient"/> via
/// <c>Microsoft.Agents.AI.Anthropic</c>'s <c>AnthropicClientExtensions.AsIChatClient</c>.
/// </summary>
/// <remarks>
/// The underlying <see cref="HttpClient"/> is obtained from <see cref="IHttpClientFactory"/>
/// under the <see cref="HttpClientNames.Anthropic"/> name so the standard resilience
/// pipeline (timeouts + retries with exponential back-off + circuit breaker) registered
/// in <c>Program.cs</c> is in effect on every Anthropic request. Step-26 (P2-K) replaced
/// the previous handler-less <see cref="AnthropicClient"/> construction with this factory
/// path so retries are governed by <c>Microsoft.Extensions.Http.Resilience</c> instead of
/// ad-hoc logic.
/// </remarks>
public sealed class AnthropicChatClientFactory : IAgentChatClientFactory
{
    private const int DefaultMaxTokens = 32_000;

    private readonly SecretsBundle _secrets;
    private readonly IHttpClientFactory _httpClientFactory;
    private AnthropicClient? _client;

    public AnthropicChatClientFactory(SecretsBundle secrets, IHttpClientFactory httpClientFactory)
    {
        _secrets = secrets;
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
            var http = _httpClientFactory.CreateClient(HttpClientNames.Anthropic);
            _client = new AnthropicClient
            {
                ApiKey = _secrets.AnthropicApiKey,
                HttpClient = http,
            };
        }
        return _client.AsIChatClient(modelId, DefaultMaxTokens);
    }
}
