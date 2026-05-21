namespace DeveloperAgent.Lifecycle;

/// <summary>
/// Thread-safe, single-slot in-process implementation of <see cref="ITaskStateStore"/>.
/// <c>Set</c> overwrites; <c>Clear</c> nulls. Reads via <see cref="Current"/> may occur
/// on any thread (e.g. from the <c>/info</c> HTTP endpoint) while the lifecycle loop
/// writes on its background thread, hence the lock.
/// </summary>
public sealed class InMemoryTaskStateStore : ITaskStateStore
{
    private readonly Lock _lock = new();
    private TaskState? _current;

    /// <inheritdoc/>
    public TaskState? Current
    {
        get
        {
            lock (_lock)
                return _current;
        }
    }

    /// <inheritdoc/>
    public void Set(TaskState state)
    {
        lock (_lock)
            _current = state;
    }

    /// <inheritdoc/>
    public void Clear()
    {
        lock (_lock)
            _current = null;
    }
}
