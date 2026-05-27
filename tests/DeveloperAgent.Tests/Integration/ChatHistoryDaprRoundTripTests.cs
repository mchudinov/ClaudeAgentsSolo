using Dapr.Client;
using DeveloperAgent.AgentMemory;
using Microsoft.Extensions.AI;
using Xunit;

namespace DeveloperAgent.Tests.Integration;

/// <summary>
/// Integration test for Step-19: round-trips a chat-history payload through the Dapr
/// state store named <c>agent-state-store</c> via the production
/// <see cref="DaprChatHistoryStore"/> path. Gated behind <c>DAPR_INTEGRATION=1</c> so
/// the default test run never tries to reach a sidecar — same gate as
/// <see cref="AgentSessionDaprRoundTripTests"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ChatHistoryDaprRoundTripTests
{
    private const string EnvGate = "DAPR_INTEGRATION";

    [SkippableFact]
    public async Task Save_Load_then_Delete_round_trips_chat_history_through_agent_state_store()
    {
        var reason = EnvironmentSkip.ReasonIfMissing(EnvGate);
        Skip.If(reason is not null, reason ?? string.Empty);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Unique per-run agent id so concurrent CI runs do not collide and so any stale
        // value cannot accidentally satisfy the assertion.
        var agentId = $"step-19-agent-{Guid.NewGuid():N}";
        var projectItemId = $"PVTI_step19_{Guid.NewGuid():N}";

        using var dapr = new DaprClientBuilder().Build();
        var adapter = new DaprClientStateAdapter(dapr);
        var store = new DaprChatHistoryStore(adapter, DaprChatHistoryStore.StateStoreName, agentId);

        var written = new List<ChatMessage>
        {
            new(ChatRole.System, "persona-line"),
            new(ChatRole.User, "hello"),
            new(ChatRole.Assistant, "hi there"),
        };

        try
        {
            await store.SaveAsync(agentId, projectItemId, written, cts.Token);

            var loaded = await store.LoadAsync(agentId, projectItemId, cts.Token);

            loaded.Should().NotBeNull(
                because: "the chat history written via SaveAsync to 'agent-state-store' must be " +
                         "returned by LoadAsync — proves the component, sidecar, key formatter, " +
                         "and Redis backing store are wired correctly.");
            loaded!.Should().HaveCount(written.Count);
            loaded.Select(m => m.Text).Should().Equal(written.Select(m => m.Text));
        }
        finally
        {
            // Best-effort cleanup — failure here does not invalidate the test outcome.
            try
            {
                await store.DeleteAsync(agentId, projectItemId, cts.Token);

                var afterDelete = await store.LoadAsync(agentId, projectItemId, cts.Token);
                afterDelete.Should().BeNull(
                    because: "DeleteAsync must remove the chat history from the state store");
            }
            catch
            {
                // Cleanup is opportunistic.
            }
        }
    }
}
