using Anthropic.SDK.Messaging;
using DeveloperAgent.GitHub;

namespace DeveloperAgent.Agent;

/// <summary>
/// Holds the mutable state for a single agent run: message history and counters.
/// Created per <see cref="IAgentRunner.RunAsync"/> call and discarded when the method returns.
/// </summary>
public sealed class AgentSession
{
    /// <summary>The full conversation history sent to and received from the model.</summary>
    public List<Message> History { get; } = [];

    /// <summary>Number of model turns (request/response cycles) consumed so far.</summary>
    public int TurnsUsed { get; set; }

    /// <summary>Total tool invocations across all turns.</summary>
    public int ToolCallsUsed { get; set; }

    /// <summary>The last text-only response from the assistant, if any.</summary>
    public string? FinalAssistantText { get; set; }

    /// <summary>The <see cref="PullRequest"/> created by the <c>create_pull_request</c> tool, if called.</summary>
    public PullRequest? CreatedPullRequest { get; set; }
}
