using Microsoft.Extensions.AI;

namespace Agent.Memory.Tests;

/// <summary>
/// Verifies the in-memory <see cref="IChatHistoryStore"/> implementation honours the
/// Save / Load / Delete contract. Used by unit tests that exercise
/// <see cref="DaprChatHistoryProvider"/> without a running Dapr sidecar.
/// </summary>
public sealed class InMemoryChatHistoryStoreTests
{
    private const string AgentId = "host-1";
    private const string ProjectItemId = "PVTI_1";

    private static IReadOnlyList<ChatMessage> Messages(params string[] texts) =>
        texts.Select(t => new ChatMessage(ChatRole.User, t)).ToList();

    [Fact]
    public async Task Load_returns_null_when_nothing_saved()
    {
        var store = new InMemoryChatHistoryStore();

        var result = await store.LoadAsync(AgentId, "PVTI_unknown", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Save_then_Load_returns_equivalent_messages()
    {
        var store = new InMemoryChatHistoryStore();
        var written = Messages("one", "two", "three");

        await store.SaveAsync(AgentId, ProjectItemId, written, CancellationToken.None);
        var loaded = await store.LoadAsync(AgentId, ProjectItemId, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Select(m => m.Text).Should().Equal("one", "two", "three");
    }

    [Fact]
    public async Task Save_overwrites_previous_list_for_same_key()
    {
        var store = new InMemoryChatHistoryStore();

        await store.SaveAsync(AgentId, ProjectItemId, Messages("first"), CancellationToken.None);
        await store.SaveAsync(AgentId, ProjectItemId, Messages("second", "third"), CancellationToken.None);
        var loaded = await store.LoadAsync(AgentId, ProjectItemId, CancellationToken.None);

        loaded!.Select(m => m.Text).Should().Equal("second", "third");
    }

    [Fact]
    public async Task Save_takes_a_defensive_copy_so_caller_mutations_do_not_leak()
    {
        var store = new InMemoryChatHistoryStore();
        var written = new List<ChatMessage>(Messages("one", "two"));

        await store.SaveAsync(AgentId, ProjectItemId, written, CancellationToken.None);
        written.Add(new ChatMessage(ChatRole.User, "post-save mutation"));

        var loaded = await store.LoadAsync(AgentId, ProjectItemId, CancellationToken.None);
        loaded!.Should().HaveCount(2,
            because: "the store must snapshot at SaveAsync time, not alias the caller's list");
    }

    [Fact]
    public async Task Delete_removes_the_list()
    {
        var store = new InMemoryChatHistoryStore();

        await store.SaveAsync(AgentId, ProjectItemId, Messages("one"), CancellationToken.None);
        await store.DeleteAsync(AgentId, ProjectItemId, CancellationToken.None);
        var loaded = await store.LoadAsync(AgentId, ProjectItemId, CancellationToken.None);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task Delete_is_a_noop_when_no_list_exists()
    {
        var store = new InMemoryChatHistoryStore();

        var act = async () => await store.DeleteAsync(AgentId, "PVTI_missing", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Lists_are_keyed_by_both_agentId_and_projectItemId()
    {
        var store = new InMemoryChatHistoryStore();

        await store.SaveAsync("host-A", "PVTI_1", Messages("A-1"), CancellationToken.None);
        await store.SaveAsync("host-B", "PVTI_1", Messages("B-1"), CancellationToken.None);
        await store.SaveAsync("host-A", "PVTI_2", Messages("A-2"), CancellationToken.None);

        (await store.LoadAsync("host-A", "PVTI_1", CancellationToken.None))!
            .Single().Text.Should().Be("A-1");
        (await store.LoadAsync("host-B", "PVTI_1", CancellationToken.None))!
            .Single().Text.Should().Be("B-1");
        (await store.LoadAsync("host-A", "PVTI_2", CancellationToken.None))!
            .Single().Text.Should().Be("A-2");
    }
}
