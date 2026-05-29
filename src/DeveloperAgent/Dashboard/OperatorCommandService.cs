using DeveloperAgent.Lifecycle;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Dashboard;

/// <summary>
/// Default <see cref="IOperatorCommandService"/>. This is the thin command surface
/// described in Step-27 (P2-L): it validates that a task is active, records the operator's
/// intent and logs it. It does <b>not</b> yet wire through to actor-level pause/resume —
/// that requires a cooperative-cancellation hook in the agent loop and a paused phase in
/// the state machine, both out of scope for this slice. The boundary is documented in the
/// PR so the forwarding can be added without changing the dashboard or this interface.
/// </summary>
public sealed class OperatorCommandService : IOperatorCommandService
{
    private readonly ITaskStateStore _stateStore;
    private readonly ILogger<OperatorCommandService> _logger;
    private readonly Lock _lock = new();
    private OperatorCommandResult? _lastCommand;

    public OperatorCommandService(ITaskStateStore stateStore, ILogger<OperatorCommandService> logger)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(logger);
        _stateStore = stateStore;
        _logger = logger;
    }

    /// <inheritdoc/>
    public OperatorCommandResult? LastCommand
    {
        get
        {
            lock (_lock)
                return _lastCommand;
        }
    }

    /// <inheritdoc/>
    public Task<OperatorCommandResult> PauseAsync(CancellationToken ct) => RecordAsync(OperatorCommand.Pause);

    /// <inheritdoc/>
    public Task<OperatorCommandResult> ResumeAsync(CancellationToken ct) => RecordAsync(OperatorCommand.Resume);

    /// <inheritdoc/>
    public Task<OperatorCommandResult> CancelAsync(CancellationToken ct) => RecordAsync(OperatorCommand.Cancel);

    private Task<OperatorCommandResult> RecordAsync(OperatorCommand command)
    {
        var current = _stateStore.Current;
        OperatorCommandResult result;

        if (current is null)
        {
            result = new OperatorCommandResult(command, Accepted: false, ProjectItemId: null, DateTimeOffset.UtcNow);
            _logger.LogWarning("Operator {Command} ignored: no active task.", command);
            return Task.FromResult(result);
        }

        result = new OperatorCommandResult(command, Accepted: true, current.ProjectItemId, DateTimeOffset.UtcNow);
        lock (_lock)
        {
            _lastCommand = result;
        }

        _logger.LogInformation(
            "Operator {Command} recorded for project item {ProjectItemId} (issue #{IssueNumber}).",
            command, current.ProjectItemId, current.IssueNumber);

        return Task.FromResult(result);
    }
}
