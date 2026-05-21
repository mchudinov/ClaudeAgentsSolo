namespace DeveloperAgent.Lifecycle;

/// <summary>
/// Single-slot store for the currently executing task.
/// Phase-1 implementation is in-memory; phase-2 will back this with a Dapr Actor.
/// </summary>
public interface ITaskStateStore
{
    /// <summary>Returns the current task state, or <see langword="null"/> when idle.</summary>
    TaskState? Current { get; }

    /// <summary>Replaces (or sets) the current task state.</summary>
    void Set(TaskState state);

    /// <summary>Clears the current task state (marks the agent as idle).</summary>
    void Clear();
}
