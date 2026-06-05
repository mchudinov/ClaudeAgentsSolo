using DeveloperAgent.Actors;
using DeveloperAgent.Lifecycle;

namespace DeveloperAgent.Tests.Dashboard;

/// <summary>Minimal test double for rendering the dashboard against a known state.</summary>
internal sealed class FakeTaskStateStore : ITaskStateStore
{
    public FakeTaskStateStore(TaskState? current = null) => Current = current;

    public TaskState? Current { get; private set; }

    public void Set(TaskState state) => Current = state;

    public void Clear() => Current = null;

    public Task<ProgrammingTaskState?> TryGetPersistedStateAsync(string projectItemId, CancellationToken ct) =>
        Task.FromResult<ProgrammingTaskState?>(null);
}
