namespace DeveloperAgent.Dashboard;

/// <summary>Operator-initiated control intent dispatched from the dashboard.</summary>
public enum OperatorCommand
{
    /// <summary>Request that the active task pause after its current step.</summary>
    Pause,

    /// <summary>Request that a paused task resume.</summary>
    Resume,

    /// <summary>Request that the active task be cancelled.</summary>
    Cancel
}

/// <summary>
/// Outcome of an operator command dispatch. <see cref="Accepted"/> is <see langword="false"/>
/// when there is no active task to apply the command to.
/// </summary>
public sealed record OperatorCommandResult(
    OperatorCommand Command,
    bool Accepted,
    string? ProjectItemId,
    DateTimeOffset RequestedAtUtc);
