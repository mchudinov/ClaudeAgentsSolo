using DeveloperAgent.GitHub;
using DeveloperAgent.Workspace;

namespace DeveloperAgent.Agent.Tools;

/// <summary>
/// The developer-agent's per-run tool context. Implements the generic <see cref="IToolContext"/>
/// seam (exposing the workspace root the generic file tools need) while also carrying the policy
/// fields the host-specific tools use: the run's session counters/PR, the prepared task workspace,
/// and the GitHub Project item being implemented. Host-specific tools downcast the
/// <see cref="IToolContext"/> they receive back to this type to reach those fields.
/// </summary>
/// <param name="Session">The active agent session (counters, created PR, latched violation).</param>
/// <param name="Workspace">The prepared task workspace.</param>
/// <param name="Item">The GitHub Project item being implemented.</param>
public sealed record ToolContext(
    AgentRunState Session,
    TaskWorkspace Workspace,
    ProjectItem Item) : IToolContext
{
    /// <inheritdoc/>
    public string WorkspaceRoot => Workspace.RepoRoot;
}
