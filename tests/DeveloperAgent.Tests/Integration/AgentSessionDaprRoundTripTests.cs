using Dapr.Client;
using Xunit;

namespace DeveloperAgent.Tests.Integration;

/// <summary>
/// Integration test for Step-18: round-trips an <see cref="AgentSession"/> through the
/// Dapr state store named <c>agent-state-store</c> via the production
/// <see cref="DaprAgentSessionStore"/> path. Gated behind <c>DAPR_INTEGRATION=1</c> so
/// the default test run never tries to reach a sidecar — same gate as the existing
/// <see cref="DaprStateRoundTripTests"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AgentSessionDaprRoundTripTests
{
    private const string EnvGate = "DAPR_INTEGRATION";

    [SkippableFact]
    public async Task Save_Load_then_Delete_round_trips_AgentSession_through_agent_state_store()
    {
        var reason = EnvironmentSkip.ReasonIfMissing(EnvGate);
        Skip.If(reason is not null, reason ?? string.Empty);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Unique per-run agent id so concurrent CI runs do not collide and so any stale
        // value cannot accidentally satisfy the assertion.
        var agentId = $"step-18-agent-{Guid.NewGuid():N}";
        var projectItemId = $"PVTI_step18_{Guid.NewGuid():N}";

        using var dapr = new DaprClientBuilder().Build();
        var adapter = new DaprClientStateAdapter(dapr);
        var store = new DaprAgentSessionStore(adapter, DaprAgentSessionStore.StateStoreName, agentId);

        var written = new AgentSession(
            AgentId: agentId,
            ProjectItemId: projectItemId,
            CurrentStep: nameof(Save_Load_then_Delete_round_trips_AgentSession_through_agent_state_store),
            LastActivityTimestampUtc: new DateTimeOffset(2026, 5, 28, 10, 30, 0, TimeSpan.Zero),
            Summary: "integration-test trace");

        try
        {
            await store.SaveAsync(written, cts.Token);

            var loaded = await store.LoadAsync(agentId, projectItemId, cts.Token);

            loaded.Should().NotBeNull(
                because: "the session written via SaveAsync to 'agent-state-store' must be returned " +
                         "by LoadAsync — proves the component, sidecar, key formatter, and Redis " +
                         "backing store are wired correctly.");
            loaded.Should().Be(written);
        }
        finally
        {
            // Best-effort cleanup — failure here does not invalidate the test outcome.
            try
            {
                await store.DeleteAsync(agentId, projectItemId, cts.Token);

                var afterDelete = await store.LoadAsync(agentId, projectItemId, cts.Token);
                afterDelete.Should().BeNull(
                    because: "DeleteAsync must remove the session from the state store");
            }
            catch
            {
                // Cleanup is opportunistic.
            }
        }
    }
}
