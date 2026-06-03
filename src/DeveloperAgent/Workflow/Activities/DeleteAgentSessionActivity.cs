using Dapr.Workflow;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Input for <see cref="DeleteAgentSessionActivity"/>.
/// </summary>
/// <param name="ProjectItemId">GitHub Project item id whose session should be removed.</param>
public sealed record DeleteAgentSessionActivityInput(string ProjectItemId);

/// <summary>
/// Removes the persisted <see cref="AgentSession"/> for the configured agent and the given
/// project item. Called from the workflow tail after terminal <see cref="DoneActivity"/>
/// (success or failure) so the state store does not accumulate stale sessions.
/// </summary>
public sealed class DeleteAgentSessionActivity : WorkflowActivity<DeleteAgentSessionActivityInput, object?>
{
    private readonly ILogger<DeleteAgentSessionActivity> _logger;
    private readonly IAgentSessionStore _store;
    private readonly string _agentId;

    public DeleteAgentSessionActivity(
        ILogger<DeleteAgentSessionActivity> logger,
        IAgentSessionStore store)
    {
        _logger = logger;
        _store = store;
        _agentId = Environment.MachineName;
    }

    public override async Task<object?> RunAsync(
        WorkflowActivityContext context, DeleteAgentSessionActivityInput input)
    {
        await _store.DeleteAsync(_agentId, input.ProjectItemId, CancellationToken.None).ConfigureAwait(false);

        _logger.LogDebug(
            "[{Activity}] item={ItemId}",
            nameof(DeleteAgentSessionActivity), input.ProjectItemId);

        return null;
    }
}
