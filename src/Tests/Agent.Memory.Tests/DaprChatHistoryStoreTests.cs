using Microsoft.Extensions.AI;

namespace Agent.Memory.Tests;

/// <summary>
/// Unit tests for <see cref="DaprChatHistoryStore"/> via the
/// <see cref="IDaprStateClient"/> seam. Asserts the key formatter shape
/// (<c>chat-history:{agentId}:{projectItemId}</c>) and that all three operations use
/// the configured state-store name. Mirrors <see cref="DaprAgentSessionStoreTests"/>.
/// </summary>
public sealed class DaprChatHistoryStoreTests
{
    private const string StoreName = "agent-state-store";
    private const string AgentId = "host-42";
    private const string ProjectItemId = "PVTI_abc";

    private readonly IDaprStateClient _dapr = Substitute.For<IDaprStateClient>();
    private readonly DaprChatHistoryStore _store;

    public DaprChatHistoryStoreTests()
    {
        _store = new DaprChatHistoryStore(_dapr, StoreName, AgentId);
    }

    [Fact]
    public void FormatKey_uses_chat_history_namespace()
    {
        DaprChatHistoryStore.FormatKey(AgentId, ProjectItemId)
            .Should().Be("chat-history:host-42:PVTI_abc");
    }

    [Fact]
    public void StateStoreName_is_agent_state_store()
    {
        DaprChatHistoryStore.StateStoreName.Should().Be("agent-state-store");
    }

    [Fact]
    public async Task LoadAsync_uses_chat_history_key_and_configured_store_name()
    {
        await _store.LoadAsync(AgentId, ProjectItemId, CancellationToken.None);

        await _dapr.Received(1).GetStateAsync<List<ChatMessage>>(
            StoreName,
            "chat-history:host-42:PVTI_abc",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadAsync_returns_value_from_state_client()
    {
        var expected = new List<ChatMessage> { new(ChatRole.User, "hi") };
        _dapr.GetStateAsync<List<ChatMessage>>(
                StoreName, "chat-history:host-42:PVTI_abc", Arg.Any<CancellationToken>())
            .Returns(expected);

        var actual = await _store.LoadAsync(AgentId, ProjectItemId, CancellationToken.None);

        actual.Should().NotBeNull();
        actual!.Select(m => m.Text).Should().Equal("hi");
    }

    [Fact]
    public async Task LoadAsync_returns_null_when_state_client_returns_null()
    {
        _dapr.GetStateAsync<List<ChatMessage>>(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((List<ChatMessage>?)null);

        var actual = await _store.LoadAsync(AgentId, "PVTI_missing", CancellationToken.None);

        actual.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_writes_under_the_canonical_key()
    {
        var written = new List<ChatMessage>
        {
            new(ChatRole.User, "a"),
            new(ChatRole.Assistant, "b"),
        };

        await _store.SaveAsync(AgentId, "PVTI_xyz", written, CancellationToken.None);

        await _dapr.Received(1).SaveStateAsync(
            StoreName,
            "chat-history:host-42:PVTI_xyz",
            Arg.Is<List<ChatMessage>>(m => m.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_uses_the_same_key_format_as_save_and_load()
    {
        await _store.DeleteAsync(AgentId, ProjectItemId, CancellationToken.None);

        await _dapr.Received(1).DeleteStateAsync(
            StoreName,
            "chat-history:host-42:PVTI_abc",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_and_Delete_use_identical_keys()
    {
        var written = new List<ChatMessage> { new(ChatRole.User, "x") };

        await _store.SaveAsync(AgentId, "PVTI_same", written, CancellationToken.None);
        await _store.DeleteAsync(AgentId, "PVTI_same", CancellationToken.None);

        var saveCall = _dapr.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IDaprStateClient.SaveStateAsync));
        var deleteCall = _dapr.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IDaprStateClient.DeleteStateAsync));

        var saveKey = saveCall.GetArguments()[1];
        var deleteKey = deleteCall.GetArguments()[1];

        saveKey.Should().Be(deleteKey,
            because: "Save and Delete must address the same Dapr state-store key");
    }
}
