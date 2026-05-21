using Anthropic.SDK.Messaging;

namespace DeveloperAgent.Agent;

/// <summary>
/// The response payload returned through the internal <see cref="IAnthropicClient"/> seam.
/// Wraps the SDK's <c>MessageResponse</c> with a thin typed envelope.
/// </summary>
internal sealed record AnthropicResponse(
    /// <summary>The stop reason string from the API (e.g. "end_turn", "tool_use").</summary>
    string StopReason,
    /// <summary>All content blocks in the response.</summary>
    List<ContentBase> ContentBlocks,
    /// <summary>The SDK's message object, used to append to history directly.</summary>
    Message AssistantMessage);
