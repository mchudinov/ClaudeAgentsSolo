namespace Agent.Memory;

/// <summary>
/// Persisted workflow-progress entity, one per (agent, project item) pair, stored in the
/// Dapr state store under key <c>agent-session:{AgentId}:{ProjectItemId}</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the durable counterpart to the host's per-<c>RunAsync</c> ephemeral run state
/// (<c>DeveloperAgent.Agent.AgentRunState</c>). Loaded once at the start of the host's task
/// workflow (<c>DeveloperAgent.Workflow.DeveloperTaskWorkflow</c>) and written between each
/// workflow activity so that a process restart can pick up where the last activity
/// finished.
/// </para>
/// <para>
/// Distinct concept from <see cref="Microsoft.Agents.AI.AgentSession"/>, the MAF
/// conversation-history primitive — this record carries workflow progress, not chat
/// turns. Step-20 will populate <see cref="Summary"/> via memory compaction; Step-18
/// leaves it null or a short trace line.
/// </para>
/// </remarks>
/// <param name="AgentId">Identity of the agent process that owns the session (typically <c>Environment.MachineName</c>).</param>
/// <param name="ProjectItemId">GitHub Project item id (<c>PVTI_*</c>) — same id used by the workflow instance.</param>
/// <param name="CurrentStep">Name of the most-recently-completed workflow activity (e.g., <c>"AcquireTaskActivity"</c>).</param>
/// <param name="LastActivityTimestampUtc">When <see cref="CurrentStep"/> was recorded.</param>
/// <param name="Summary">Compacted prior-context summary; null until Step-20 wires the compaction provider.</param>
public sealed record AgentSession(
    string AgentId,
    string ProjectItemId,
    string CurrentStep,
    DateTimeOffset LastActivityTimestampUtc,
    string? Summary);
