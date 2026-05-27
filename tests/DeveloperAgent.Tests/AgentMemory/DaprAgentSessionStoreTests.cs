using DeveloperAgent.AgentMemory;

namespace DeveloperAgent.Tests.AgentMemory;

/// <summary>
/// Unit tests for <see cref="DaprAgentSessionStore"/> via the
/// <see cref="IDaprStateClient"/> seam. Asserts the key formatter and that all
/// three operations use the configured state-store name.
/// </summary>
public sealed class DaprAgentSessionStoreTests
{
    private const string StoreName = "agent-state-store";
    private const string AgentId = "host-42";

    private readonly IDaprStateClient _dapr = Substitute.For<IDaprStateClient>();
    private readonly DaprAgentSessionStore _store;

    public DaprAgentSessionStoreTests()
    {
        _store = new DaprAgentSessionStore(_dapr, StoreName, AgentId);
    }

    private static AgentSession MakeSession(string itemId = "PVTI_abc") =>
        new(AgentId, itemId, "AcquireTaskActivity",
            new DateTimeOffset(2026, 5, 28, 12, 0, 0, TimeSpan.Zero), Summary: null);

    [Fact]
    public async Task LoadAsync_uses_agent_session_key_format_and_configured_store_name()
    {
        await _store.LoadAsync(AgentId, "PVTI_abc", CancellationToken.None);

        await _dapr.Received(1).GetStateAsync<AgentSession>(
            StoreName,
            "agent-session:host-42:PVTI_abc",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadAsync_returns_value_from_state_client()
    {
        var expected = MakeSession();
        _dapr.GetStateAsync<AgentSession>(
                StoreName, "agent-session:host-42:PVTI_abc", Arg.Any<CancellationToken>())
            .Returns(expected);

        var actual = await _store.LoadAsync(AgentId, "PVTI_abc", CancellationToken.None);

        actual.Should().Be(expected);
    }

    [Fact]
    public async Task LoadAsync_returns_null_when_state_client_returns_null()
    {
        _dapr.GetStateAsync<AgentSession>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentSession?)null);

        var actual = await _store.LoadAsync(AgentId, "PVTI_missing", CancellationToken.None);

        actual.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_writes_the_session_under_the_canonical_key()
    {
        var session = MakeSession("PVTI_xyz");

        await _store.SaveAsync(session, CancellationToken.None);

        await _dapr.Received(1).SaveStateAsync(
            StoreName,
            "agent-session:host-42:PVTI_xyz",
            session,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_uses_the_same_key_format_as_save_and_load()
    {
        await _store.DeleteAsync(AgentId, "PVTI_abc", CancellationToken.None);

        await _dapr.Received(1).DeleteStateAsync(
            StoreName,
            "agent-session:host-42:PVTI_abc",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_and_Delete_use_identical_keys()
    {
        var session = MakeSession("PVTI_same");

        await _store.SaveAsync(session, CancellationToken.None);
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
