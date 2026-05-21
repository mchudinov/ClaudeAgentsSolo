using DeveloperAgent.GitHub;
using DeveloperAgent.Workspace;

namespace DeveloperAgent.Agent.Tools;

/// <summary>
/// Carries the dependencies a tool needs to perform its work.
/// Passed to every <see cref="ITool.InvokeAsync"/> call.
/// </summary>
/// <param name="Session">The active agent session (history, counters, created PR).</param>
/// <param name="Workspace">The prepared task workspace.</param>
/// <param name="Item">The GitHub Project item being implemented.</param>
public sealed record ToolContext(
    AgentSession Session,
    TaskWorkspace Workspace,
    ProjectItem Item);
