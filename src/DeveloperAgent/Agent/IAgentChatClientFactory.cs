using Microsoft.Extensions.AI;

namespace DeveloperAgent.Agent;

/// <summary>
/// Creates a fresh <see cref="IChatClient"/> for each agent run.
/// <para>
/// A factory rather than a singleton because the chat client wraps Anthropic SDK state
/// (HTTP pipeline, retries, etc.) that the runner wraps further with per-run decorators
/// (<see cref="TurnCountingChatClient"/>). Production resolves this to an Anthropic-backed
/// implementation; unit tests substitute it to inject a NSubstitute <see cref="IChatClient"/>.
/// </para>
/// </summary>
public interface IAgentChatClientFactory
{
    /// <summary>Create a chat client that targets the given Anthropic model id.</summary>
    /// <param name="modelId">The model name (e.g. <c>claude-opus-4-7</c>).</param>
    IChatClient Create(string modelId);
}
