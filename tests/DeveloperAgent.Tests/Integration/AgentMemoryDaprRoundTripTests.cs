using Dapr.Client;
using Xunit;

namespace DeveloperAgent.Tests.Integration;

/// <summary>
/// Integration test for Step-20: round-trips both memory namespaces
/// (<c>repo-state:{repoId}</c> and <c>task-memory:{projectItemId}</c>) through the Dapr state
/// store named <c>agent-state-store</c> via the production <see cref="DaprAgentMemoryStore"/>.
/// Gated behind <c>DAPR_INTEGRATION=1</c> so the default test run never tries to reach a
/// sidecar — same gate as <see cref="ChatHistoryDaprRoundTripTests"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AgentMemoryDaprRoundTripTests
{
    private const string EnvGate = "DAPR_INTEGRATION";

    [SkippableFact]
    public async Task Repo_and_task_memories_round_trip_through_agent_state_store()
    {
        var reason = EnvironmentSkip.ReasonIfMissing(EnvGate);
        Skip.If(reason is not null, reason ?? string.Empty);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Unique ids per run so concurrent CI runs do not collide and stale values cannot
        // accidentally satisfy the assertion.
        var repoId = $"step-20-repo-{Guid.NewGuid():N}";
        var projectItemId = $"PVTI_step20_{Guid.NewGuid():N}";

        using var dapr = new DaprClientBuilder().Build();
        var adapter = new DaprClientStateAdapter(dapr);
        var store = new DaprAgentMemoryStore(adapter, DaprAgentMemoryStore.StateStoreName);

        var repoMemories = new List<string> { "Repository uses xUnit", "Main branch is protected" };
        var taskMemories = new List<string> { "Tests live under tests/DeveloperAgent.Tests" };

        try
        {
            await store.SaveRepoMemoriesAsync(repoId, repoMemories, cts.Token);
            await store.SaveTaskMemoriesAsync(projectItemId, taskMemories, cts.Token);

            var loadedRepo = await store.LoadRepoMemoriesAsync(repoId, cts.Token);
            var loadedTask = await store.LoadTaskMemoriesAsync(projectItemId, cts.Token);

            loadedRepo.Should().NotBeNull(
                because: "repo memories written to 'agent-state-store' under repo-state:{repoId} " +
                         "must be returned by LoadRepoMemoriesAsync — proves component, sidecar, " +
                         "key formatter, and Redis backing store are wired correctly.");
            loadedRepo!.Should().Equal(repoMemories);

            loadedTask.Should().NotBeNull(
                because: "task memories under task-memory:{projectItemId} must round-trip too.");
            loadedTask!.Should().Equal(taskMemories);
        }
        finally
        {
            // Best-effort cleanup — failure here does not invalidate the test outcome.
            try
            {
                await store.DeleteRepoMemoriesAsync(repoId, cts.Token);
                await store.DeleteTaskMemoriesAsync(projectItemId, cts.Token);

                (await store.LoadRepoMemoriesAsync(repoId, cts.Token)).Should().BeNull(
                    because: "DeleteRepoMemoriesAsync must remove the record from the state store");
                (await store.LoadTaskMemoriesAsync(projectItemId, cts.Token)).Should().BeNull(
                    because: "DeleteTaskMemoriesAsync must remove the record from the state store");
            }
            catch
            {
                // Cleanup is opportunistic.
            }
        }
    }
}
