using DeveloperAgent.Lifecycle;

namespace DeveloperAgent.Tests.Lifecycle;

public sealed class InMemoryTaskStateStoreTests
{
    private static TaskState MakeState(string id = "item-1") =>
        new(
            ProjectItemId: id,
            IssueNumber: 1,
            Title: "Test task",
            Phase: TaskPhase.Acquired,
            BranchName: null,
            PullRequestNumber: null,
            LastError: null,
            StartedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            PullRequestOpenedAtUtc: null,
            LastReviewPolledAtUtc: null);

    [Fact]
    public void Empty_store_returns_null()
    {
        var store = new InMemoryTaskStateStore();
        store.Current.Should().BeNull();
    }

    [Fact]
    public void Set_then_Current_returns_set_value()
    {
        var store = new InMemoryTaskStateStore();
        var state = MakeState("item-42");

        store.Set(state);

        store.Current.Should().BeSameAs(state);
    }

    [Fact]
    public void Set_overwrites_previous_value()
    {
        var store = new InMemoryTaskStateStore();
        var first = MakeState("item-1");
        var second = MakeState("item-2");

        store.Set(first);
        store.Set(second);

        store.Current.Should().BeSameAs(second);
    }

    [Fact]
    public void Clear_returns_to_null()
    {
        var store = new InMemoryTaskStateStore();
        store.Set(MakeState());

        store.Clear();

        store.Current.Should().BeNull();
    }

    [Fact]
    public void Concurrent_reads_and_writes_dont_tear()
    {
        var store = new InMemoryTaskStateStore();

        // 100 concurrent sets interleaved with reads should not throw.
        Parallel.For(0, 100, i =>
        {
            store.Set(MakeState($"item-{i}"));
            _ = store.Current; // concurrent read
        });

        // After all writes, Current should be non-null.
        store.Current.Should().NotBeNull();
    }
}
