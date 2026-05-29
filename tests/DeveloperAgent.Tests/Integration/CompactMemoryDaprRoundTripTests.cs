using Dapr.Client;
using DeveloperAgent.AgentMemory;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using DeveloperAgent.Workflow.Activities;
using Xunit;

namespace DeveloperAgent.Tests.Integration;

/// <summary>
/// Integration test for Step-25 (P2-J): demonstrates the compaction-after-Done round-trip.
/// <see cref="CompactMemoryActivity"/> writes the task-completion summary under
/// <c>task-memory:{projectItemId}</c> through the production <see cref="DaprAgentMemoryStore"/>;
/// a subsequent run on the same project item — modelled by the Step-20
/// <see cref="DaprAgentMemoryContextProvider"/> — reads that summary back and injects it into the
/// agent's <see cref="AIContext.Instructions"/>.
/// </summary>
/// <remarks>
/// task-memory is keyed per project item, so the "next task in the same repo" channel for a
/// genuinely different item is repo-memory, not task-memory. This test exercises the namespace
/// the issue names (<c>task-memory:{projectItemId}</c>): the same project item id is read back by
/// the provider, proving component + sidecar + key formatter + Redis are wired end-to-end.
/// Gated behind <c>DAPR_INTEGRATION=1</c> — same gate as <see cref="AgentMemoryDaprRoundTripTests"/>.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CompactMemoryDaprRoundTripTests
{
    private const string EnvGate = "DAPR_INTEGRATION";

    /// <summary>Memory extractor that never produces new memories — isolates the read path.</summary>
    // Helper types live in the .Integration namespace; the IntegrationTraitConventionTests
    // convention scans every concrete class there, so they carry the trait too.
    [Trait("Category", "Integration")]
    private sealed class NoOpExtractor : IMemoryExtractor
    {
        public ValueTask<ExtractedMemories> ExtractAsync(
            IReadOnlyList<ChatMessage> conversation, CancellationToken ct) =>
            ValueTask.FromResult(ExtractedMemories.Empty);
    }

    [Trait("Category", "Integration")]
    private sealed class FakeActivityContext : Dapr.Workflow.WorkflowActivityContext
    {
        public override Dapr.Workflow.Abstractions.TaskIdentifier Identifier => "compact";
        public override string InstanceId => "github-project-item-integration";
        public override string TaskExecutionKey => "exec-key";
    }

    [SkippableFact]
    public async Task Compaction_summary_is_read_back_by_the_memory_context_provider()
    {
        var reason = EnvironmentSkip.ReasonIfMissing(EnvGate);
        Skip.If(reason is not null, reason ?? string.Empty);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var projectItemId = $"PVTI_step25_{Guid.NewGuid():N}";
        const string repoId = "octo/repo"; // unused namespace for this item — provider returns null

        using var dapr = new DaprClientBuilder().Build();
        var adapter = new DaprClientStateAdapter(dapr);
        var store = new DaprAgentMemoryStore(adapter, DaprAgentMemoryStore.StateStoreName);

        var activity = new CompactMemoryActivity(
            NullLogger<CompactMemoryActivity>.Instance,
            store,
            new FakeTimeProvider(new DateTimeOffset(2026, 5, 30, 12, 0, 0, TimeSpan.Zero)));

        var input = new CompactMemoryActivityInput(
            ProjectItemId: projectItemId,
            Title: "Implement compaction after Done",
            ContentNumber: 25,
            BranchName: "Step-25-compaction-after-done",
            PullRequestNumber: 51,
            ToolCallsUsed: 9,
            Decisions: "Composed a deterministic structured summary inside the activity.",
            ChangedFiles: new[] { "src/DeveloperAgent/Workflow/Activities/CompactMemoryActivity.cs" },
            TestResults: "all green",
            UnresolvedRisks: "None");

        try
        {
            // 1. Compaction writes the summary (the post-Done step on the prior run).
            await activity.RunAsync(new FakeActivityContext(), input);

            // 2. A subsequent run on the same item reads it back via the Step-20 provider.
            var provider = new DaprAgentMemoryContextProvider(
                store,
                new NoOpExtractor(),
                repoId: repoId,
                projectItemId: projectItemId,
                maxInjectedPerScope: 5,
                maxStoredPerScope: 20);

            // ProvideAIContextAsync is protected — drive it through the public InvokingAsync
            // entry point exactly as DaprAgentMemoryContextProviderTests does.
#pragma warning disable MAAI001
            var agent = NSubstitute.Substitute.For<AIAgent>();
            var session = NSubstitute.Substitute.For<Microsoft.Agents.AI.AgentSession>();
            var invokingCtx = new AIContextProvider.InvokingContext(agent, session, new AIContext());
            var ctx = await provider.InvokingAsync(invokingCtx, cts.Token);
#pragma warning restore MAAI001

            ctx.Instructions.Should().NotBeNullOrEmpty(
                because: "the compaction summary persisted under task-memory:{projectItemId} must be " +
                         "injected by DaprAgentMemoryContextProvider on the next run.");
            ctx.Instructions!.Should().Contain("Implement compaction after Done");
            ctx.Instructions!.Should().Contain("Completed task");
        }
        finally
        {
            try
            {
                await store.DeleteTaskMemoriesAsync(projectItemId, cts.Token);
            }
            catch
            {
                // Cleanup is opportunistic.
            }
        }
    }
}
