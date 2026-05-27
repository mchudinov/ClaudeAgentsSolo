using Dapr.Workflow;
using DeveloperAgent.AgentMemory;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Input for <see cref="SaveAgentSessionActivity"/>.
/// </summary>
/// <param name="ProjectItemId">GitHub Project item id whose session is being updated.</param>
/// <param name="CurrentStep">Name of the workflow activity that just completed (e.g. <c>"AcquireTaskActivity"</c>).</param>
/// <param name="Summary">Optional compacted-context summary; Step-18 always passes null.</param>
public sealed record SaveAgentSessionActivityInput(
    string ProjectItemId,
    string CurrentStep,
    string? Summary);

/// <summary>
/// Persists the workflow-progress <see cref="AgentSession"/> after a workflow activity completes.
/// The activity stamps <see cref="AgentSession.LastActivityTimestampUtc"/> itself (workflow code
/// must be deterministic — no <c>DateTimeOffset.UtcNow</c> in the workflow body).
/// </summary>
public sealed class SaveAgentSessionActivity : WorkflowActivity<SaveAgentSessionActivityInput, object?>
{
    private readonly ILogger<SaveAgentSessionActivity> _logger;
    private readonly IAgentSessionStore _store;
    private readonly TimeProvider _clock;
    private readonly string _agentId;

    public SaveAgentSessionActivity(
        ILogger<SaveAgentSessionActivity> logger,
        IAgentSessionStore store,
        TimeProvider clock)
    {
        _logger = logger;
        _store = store;
        _clock = clock;
        _agentId = Environment.MachineName;
    }

    public override async Task<object?> RunAsync(
        WorkflowActivityContext context, SaveAgentSessionActivityInput input)
    {
        var session = new AgentSession(
            AgentId: _agentId,
            ProjectItemId: input.ProjectItemId,
            CurrentStep: input.CurrentStep,
            LastActivityTimestampUtc: _clock.GetUtcNow(),
            Summary: input.Summary);

        await _store.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);

        _logger.LogDebug(
            "[{Activity}] item={ItemId} step={Step}",
            nameof(SaveAgentSessionActivity), input.ProjectItemId, input.CurrentStep);

        return null;
    }
}
