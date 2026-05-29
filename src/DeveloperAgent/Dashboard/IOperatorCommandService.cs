namespace DeveloperAgent.Dashboard;

/// <summary>
/// Command surface the operator dashboard dispatches to for pause / resume / cancel.
/// Kept deliberately separate from <c>ITaskStateStore</c>: the per-item state machine
/// (<c>TaskPhase</c>) has no paused/cancelled phase and <c>ITaskStateStore.Clear</c> has
/// actor-claim side effects, so a dashboard button must not drive them directly.
/// </summary>
public interface IOperatorCommandService
{
    /// <summary>Most recently dispatched command, or <see langword="null"/> when none has been issued.</summary>
    OperatorCommandResult? LastCommand { get; }

    /// <summary>Requests that the active task pause.</summary>
    Task<OperatorCommandResult> PauseAsync(CancellationToken ct);

    /// <summary>Requests that a paused task resume.</summary>
    Task<OperatorCommandResult> ResumeAsync(CancellationToken ct);

    /// <summary>Requests that the active task be cancelled.</summary>
    Task<OperatorCommandResult> CancelAsync(CancellationToken ct);
}
