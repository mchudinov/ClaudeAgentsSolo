namespace DeveloperAgent.Lifecycle;

/// <summary>High-level outcome of a single per-item task execution.</summary>
public enum TaskOutcome
{
    /// <summary>PR merged and item moved to Done.</summary>
    Done,

    /// <summary>Terminal failure; item released back to Ready with a comment.</summary>
    Failed,

    /// <summary>Execution was cancelled via the stopping token.</summary>
    Cancelled
}

/// <summary>Return value of <see cref="TaskExecutor.RunAsync"/>.</summary>
/// <param name="Outcome">How the task ended.</param>
public sealed record TaskExecutionResult(TaskOutcome Outcome);
