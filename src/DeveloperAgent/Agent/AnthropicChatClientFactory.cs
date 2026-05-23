using Anthropic;
using DeveloperAgent.Configuration;
using Microsoft.Extensions.AI;

namespace DeveloperAgent.Agent;

/// <summary>
/// Production <see cref="IAgentChatClientFactory"/> backed by the official Anthropic .NET SDK
/// (the <c>Anthropic</c> NuGet package), exposed as an <see cref="IChatClient"/> via
/// <c>Microsoft.Agents.AI.Anthropic</c>'s <c>AnthropicClientChatClientExtensions.AsIChatClient</c>.
/// </summary>
public sealed class AnthropicChatClientFactory : IAgentChatClientFactory
{
    private const int DefaultMaxTokens = 32_000;

    private readonly SecretsBundle _secrets;
    private AnthropicClient? _client;

    public AnthropicChatClientFactory(SecretsBundle secrets)
    {
        _secrets = secrets;
    }

    public IChatClient Create(string modelId)
    {
        _client ??= new AnthropicClient { ApiKey = _secrets.AnthropicApiKey };
        return _client.AsIChatClient(modelId, DefaultMaxTokens);
    }
}
