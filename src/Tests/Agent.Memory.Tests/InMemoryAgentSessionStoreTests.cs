
namespace Agent.Memory.Tests;

/// <summary>
/// Verifies the in-memory <see cref="IAgentSessionStore"/> implementation honours the
/// Save / Load / Delete contract. Used by unit tests that exercise the workflow without
/// a Dapr sidecar.
/// </summary>
public sealed class InMemoryAgentSessionStoreTests
{
    private static AgentSession MakeSession(string agentId = "host-1", string itemId = "PVTI_1",
        string step = "AcquireTaskActivity", string? summary = null) =>
        new(agentId, itemId, step, new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero), summary);

    [Fact]
    public async Task Load_returns_null_when_no_session_saved()
    {
        var store = new InMemoryAgentSessionStore();

        var result = await store.LoadAsync("host-1", "PVTI_unknown", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Save_then_Load_returns_the_same_session()
    {
        var store = new InMemoryAgentSessionStore();
        var session = MakeSession(step: "PlanActivity", summary: "first round done");

        await store.SaveAsync(session, CancellationToken.None);
        var loaded = await store.LoadAsync(session.AgentId, session.ProjectItemId, CancellationToken.None);

        loaded.Should().Be(session);
    }

    [Fact]
    public async Task Save_overwrites_previous_session_for_same_key()
    {
        var store = new InMemoryAgentSessionStore();
        var first = MakeSession(step: "AcquireTaskActivity");
        var second = first with { CurrentStep = "CreateBranchActivity" };

        await store.SaveAsync(first, CancellationToken.None);
        await store.SaveAsync(second, CancellationToken.None);
        var loaded = await store.LoadAsync(first.AgentId, first.ProjectItemId, CancellationToken.None);

        loaded.Should().Be(second);
    }

    [Fact]
    public async Task Delete_removes_the_session()
    {
        var store = new InMemoryAgentSessionStore();
        var session = MakeSession();

        await store.SaveAsync(session, CancellationToken.None);
        await store.DeleteAsync(session.AgentId, session.ProjectItemId, CancellationToken.None);
        var loaded = await store.LoadAsync(session.AgentId, session.ProjectItemId, CancellationToken.None);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task Delete_is_a_noop_when_no_session_exists()
    {
        var store = new InMemoryAgentSessionStore();

        var act = async () => await store.DeleteAsync("host-1", "PVTI_missing", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Sessions_are_keyed_by_both_agentId_and_projectItemId()
    {
        var store = new InMemoryAgentSessionStore();
        var hostAItem1 = MakeSession(agentId: "host-A", itemId: "PVTI_1", step: "PlanActivity");
        var hostBItem1 = MakeSession(agentId: "host-B", itemId: "PVTI_1", step: "ModifyCodeActivity");
        var hostAItem2 = MakeSession(agentId: "host-A", itemId: "PVTI_2", step: "CreateBranchActivity");

        await store.SaveAsync(hostAItem1, CancellationToken.None);
        await store.SaveAsync(hostBItem1, CancellationToken.None);
        await store.SaveAsync(hostAItem2, CancellationToken.None);

        (await store.LoadAsync("host-A", "PVTI_1", CancellationToken.None)).Should().Be(hostAItem1);
        (await store.LoadAsync("host-B", "PVTI_1", CancellationToken.None)).Should().Be(hostBItem1);
        (await store.LoadAsync("host-A", "PVTI_2", CancellationToken.None)).Should().Be(hostAItem2);
    }
}
