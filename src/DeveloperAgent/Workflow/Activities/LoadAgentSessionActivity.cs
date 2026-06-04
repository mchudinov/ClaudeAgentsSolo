using Dapr.Workflow;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Input for <see cref="LoadAgentSessionActivity"/>.
/// </summary>
/// <param name="ProjectItemId">GitHub Project item id whose session should be loaded.</param>
public sealed record LoadAgentSessionActivityInput(string ProjectItemId);

/// <summary>
/// Loads the persisted <see cref="AgentSession"/> for the configured agent and the given
/// project item, or returns a freshly-initialised session if no record exists.
/// </summary>
/// <remarks>
/// Called once at the start of <see cref="DeveloperTaskWorkflow"/> (and once on the
/// recovery fast-path) so the workflow has a starting <see cref="AgentSession"/> to carry
/// across activities. The returned <c>Summary</c> is what Step-20 will plumb into the
/// agent kickoff; Step-18 only persists the session — it does not consume the summary.
/// </remarks>
public sealed class LoadAgentSessionActivity : WorkflowActivity<LoadAgentSessionActivityInput, AgentSession>
{
    private readonly ILogger<LoadAgentSessionActivity> _logger;
    private readonly IAgentSessionStore _store;
    private readonly TimeProvider _clock;
    private readonly string _agentId;

    public LoadAgentSessionActivity(
        ILogger<LoadAgentSessionActivity> logger,
        IAgentSessionStore store,
        TimeProvider clock)
    {
        _logger = logger;
        _store = store;
        _clock = clock;
        _agentId = Environment.MachineName;
    }

    public override async Task<AgentSession> RunAsync(
        WorkflowActivityContext context, LoadAgentSessionActivityInput input)
    {
        var ct = CancellationToken.None;
        var existing = await _store.LoadAsync(_agentId, input.ProjectItemId, ct).ConfigureAwait(false);

        if (existing is not null)
        {
            _logger.LogInformation(
                "[{Activity}] resumed session for item={ItemId} at step={Step}",
                nameof(LoadAgentSessionActivity), input.ProjectItemId, existing.CurrentStep);
            return existing;
        }

        var fresh = new AgentSession(
            AgentId: _agentId,
            ProjectItemId: input.ProjectItemId,
            CurrentStep: string.Empty,
            LastActivityTimestampUtc: _clock.GetUtcNow(),
            Summary: null);

        _logger.LogInformation(
            "[{Activity}] no prior session for item={ItemId} — initialised fresh",
            nameof(LoadAgentSessionActivity), input.ProjectItemId);

        return fresh;
    }
}
