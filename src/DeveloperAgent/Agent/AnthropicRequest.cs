using Anthropic.SDK.Messaging;

namespace DeveloperAgent.Agent;

/// <summary>
/// The request payload passed through the internal <see cref="IAnthropicClient"/> seam.
/// Decouples the runner from the SDK's concrete <c>MessageParameters</c> type.
/// </summary>
internal sealed record AnthropicRequest(
    string SystemPrompt,
    List<Message> Messages,
    IList<Anthropic.SDK.Common.Tool> Tools,
    string Model,
    int MaxTokens,
    decimal Temperature,
    int? ThinkingBudgetTokens);
