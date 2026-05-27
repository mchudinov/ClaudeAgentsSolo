using DeveloperAgent.Agent.Tools;
using DeveloperAgent.GitHub;
using DeveloperAgent.Workspace;

namespace DeveloperAgent.Agent;

/// <summary>
/// Holds the mutable state for a single agent run: counters, created PR, and any
/// latched sandbox violation.
/// <para>
/// Created per <see cref="IAgentRunner.RunAsync"/> call and discarded when the method returns.
/// Microsoft Agent Framework owns the chat history itself (via its internal
/// <see cref="Microsoft.Agents.AI.AgentSession"/>), so this class no longer keeps a message list.
/// </para>
/// <para>
/// NOTE: This class is the per-run ephemeral counters object. The persisted workflow-progress
/// entity is <see cref="DeveloperAgent.AgentMemory.AgentSession"/> — different concept,
/// different lifecycle.
/// </para>
/// </summary>
public sealed class AgentRunState
{
    /// <summary>Number of model turns (request/response cycles) consumed so far.</summary>
    public int TurnsUsed { get; set; }

    /// <summary>
    /// Total invocations of <em>local</em> tools (the in-process <see cref="ITool"/> adapters)
    /// across all turns. MCP-sourced tools are not counted because they are invoked directly
    /// by Microsoft Agent Framework's <c>FunctionInvokingChatClient</c> without passing through
    /// <see cref="MafToolAdapter"/>.
    /// </summary>
    public int ToolCallsUsed { get; set; }

    /// <summary>The last text-only response from the assistant, if any.</summary>
    public string? FinalAssistantText { get; set; }

    /// <summary>The <see cref="PullRequest"/> created by the <c>create_pull_request</c> tool, if called.</summary>
    public PullRequest? CreatedPullRequest { get; set; }

    /// <summary>
    /// If any <see cref="ITool"/> threw <see cref="SandboxViolationException"/> during this run,
    /// the offending tool's name and the original exception are latched here so the runner
    /// can surface an <see cref="AgentRunOutcome.SandboxViolation"/> outcome after Microsoft
    /// Agent Framework wraps the exception in an <see cref="AggregateException"/>.
    /// </summary>
    public (string ToolName, SandboxViolationException Exception)? SandboxViolation { get; set; }
}
