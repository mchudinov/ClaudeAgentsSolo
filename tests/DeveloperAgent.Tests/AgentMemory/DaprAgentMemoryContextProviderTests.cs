using DeveloperAgent.AgentMemory;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace DeveloperAgent.Tests.AgentMemory;

/// <summary>
/// Behavioural tests for <see cref="DaprAgentMemoryContextProvider"/> (LLD memory layer 3).
/// Covers the two halves of the contract: <b>inject before</b> (repo + task memories folded
/// into <see cref="AIContext.Instructions"/>, capped, empty-safe) and <b>extract after</b>
/// (new memories distilled via <see cref="IMemoryExtractor"/>, de-duplicated, capped, skipped
/// on a failed run).
/// </summary>
/// <remarks>
/// The provider's <c>ProvideAIContextAsync</c>/<c>StoreAIContextAsync</c> overrides are
/// <c>protected</c>, so tests drive them through the public <see cref="AIContextProvider.InvokingAsync"/>
/// /<see cref="AIContextProvider.InvokedAsync"/> entry points — the same approach as
/// <see cref="DaprChatHistoryProviderTests"/>. A deterministic stub extractor and an
/// <see cref="InMemoryAgentMemoryStore"/> keep the behaviour observable without Dapr or LLM traffic.
/// </remarks>
public sealed class DaprAgentMemoryContextProviderTests
{
    private const string RepoId = "octo/repo";
    private const string ProjectItemId = "PVTI_step20";

    private sealed class StubExtractor : IMemoryExtractor
    {
        private readonly ExtractedMemories _result;
        public List<IReadOnlyList<ChatMessage>> Invocations { get; } = new();

        public StubExtractor(ExtractedMemories result) => _result = result;

        public ValueTask<ExtractedMemories> ExtractAsync(IReadOnlyList<ChatMessage> conversation, CancellationToken ct)
        {
            Invocations.Add(new List<ChatMessage>(conversation));
            return ValueTask.FromResult(_result);
        }
    }

    private static ChatMessage User(string text) => new(ChatRole.User, text);
    private static ChatMessage Assistant(string text) => new(ChatRole.Assistant, text);

    private static DaprAgentMemoryContextProvider BuildProvider(
        IAgentMemoryStore store,
        IMemoryExtractor extractor,
        int maxInjectedPerScope = 10,
        int maxStoredPerScope = 50) =>
        new(store, extractor, RepoId, ProjectItemId, maxInjectedPerScope, maxStoredPerScope);

#pragma warning disable MAAI001
    private static async Task<AIContext> InvokeProvideAsync(DaprAgentMemoryContextProvider provider)
    {
        var agent = Substitute.For<AIAgent>();
        var session = Substitute.For<Microsoft.Agents.AI.AgentSession>();
        var ctx = new AIContextProvider.InvokingContext(agent, session, new AIContext());
        return await provider.InvokingAsync(ctx, CancellationToken.None);
    }

    private static async Task InvokeStoreAsync(
        DaprAgentMemoryContextProvider provider,
        IEnumerable<ChatMessage> requestMessages,
        IEnumerable<ChatMessage> responseMessages)
    {
        var agent = Substitute.For<AIAgent>();
        var session = Substitute.For<Microsoft.Agents.AI.AgentSession>();
        var ctx = new AIContextProvider.InvokedContext(agent, session, requestMessages, responseMessages);
        await provider.InvokedAsync(ctx, CancellationToken.None);
    }

    private static async Task InvokeStoreFailedAsync(
        DaprAgentMemoryContextProvider provider,
        IEnumerable<ChatMessage> requestMessages,
        Exception invokeException)
    {
        var agent = Substitute.For<AIAgent>();
        var session = Substitute.For<Microsoft.Agents.AI.AgentSession>();
        var ctx = new AIContextProvider.InvokedContext(agent, session, requestMessages, invokeException);
        await provider.InvokedAsync(ctx, CancellationToken.None);
    }
#pragma warning restore MAAI001

    [Fact]
    public void StateKeys_is_empty_because_dapr_is_storage_of_record()
    {
        var provider = BuildProvider(new InMemoryAgentMemoryStore(), new StubExtractor(ExtractedMemories.Empty));
        provider.StateKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task ProvideAIContext_with_no_memories_returns_context_without_instructions()
    {
        var provider = BuildProvider(new InMemoryAgentMemoryStore(), new StubExtractor(ExtractedMemories.Empty));

        var ctx = await InvokeProvideAsync(provider);

        ctx.Should().NotBeNull();
        ctx.Instructions.Should().BeNullOrEmpty(
            because: "with nothing persisted there is no memory block to inject");
    }

    [Fact]
    public async Task ProvideAIContext_injects_repo_and_task_memories_into_instructions()
    {
        var store = new InMemoryAgentMemoryStore();
        await store.SaveRepoMemoriesAsync(RepoId, new[] { "Repository uses xUnit" }, CancellationToken.None);
        await store.SaveTaskMemoriesAsync(ProjectItemId, new[] { "Avoid editing generated migrations" }, CancellationToken.None);
        var provider = BuildProvider(store, new StubExtractor(ExtractedMemories.Empty));

        var ctx = await InvokeProvideAsync(provider);

        ctx.Instructions.Should().NotBeNullOrEmpty();
        ctx.Instructions.Should().Contain(DaprAgentMemoryContextProvider.InstructionsHeader);
        ctx.Instructions.Should().Contain("Repository uses xUnit");
        ctx.Instructions.Should().Contain("Avoid editing generated migrations");
    }

    [Fact]
    public async Task ProvideAIContext_caps_injected_memories_to_the_most_recent_per_scope()
    {
        var store = new InMemoryAgentMemoryStore();
        // 5 repo memories, oldest first; cap of 2 should keep only the 2 most recent.
        await store.SaveRepoMemoriesAsync(RepoId,
            new[] { "r1-oldest", "r2", "r3", "r4", "r5-newest" }, CancellationToken.None);
        var provider = BuildProvider(store, new StubExtractor(ExtractedMemories.Empty), maxInjectedPerScope: 2);

        var ctx = await InvokeProvideAsync(provider);

        ctx.Instructions.Should().Contain("r5-newest");
        ctx.Instructions.Should().Contain("r4");
        ctx.Instructions.Should().NotContain("r1-oldest");
        ctx.Instructions.Should().NotContain("r2");
    }

    [Fact]
    public async Task StoreAIContext_passes_request_and_response_messages_to_the_extractor()
    {
        var extractor = new StubExtractor(ExtractedMemories.Empty);
        var provider = BuildProvider(new InMemoryAgentMemoryStore(), extractor);

        await InvokeStoreAsync(provider,
            new[] { User("do the thing") },
            new[] { Assistant("done") });

        extractor.Invocations.Should().HaveCount(1);
        extractor.Invocations[0].Select(m => m.Text).Should().Equal("do the thing", "done");
    }

    [Fact]
    public async Task StoreAIContext_appends_new_memories_to_both_namespaces()
    {
        var store = new InMemoryAgentMemoryStore();
        await store.SaveRepoMemoriesAsync(RepoId, new[] { "existing-repo" }, CancellationToken.None);
        var extractor = new StubExtractor(new ExtractedMemories(
            RepoMemories: new[] { "Main branch is protected" },
            TaskMemories: new[] { "Tests live under tests/" }));
        var provider = BuildProvider(store, extractor);

        await InvokeStoreAsync(provider, new[] { User("q") }, new[] { Assistant("a") });

        var repo = await store.LoadRepoMemoriesAsync(RepoId, CancellationToken.None);
        var task = await store.LoadTaskMemoriesAsync(ProjectItemId, CancellationToken.None);
        repo.Should().Equal("existing-repo", "Main branch is protected");
        task.Should().Equal("Tests live under tests/");
    }

    [Fact]
    public async Task StoreAIContext_does_not_duplicate_a_memory_already_persisted()
    {
        var store = new InMemoryAgentMemoryStore();
        await store.SaveRepoMemoriesAsync(RepoId, new[] { "Repository uses xUnit" }, CancellationToken.None);
        // Extractor re-surfaces the same fact (different casing) — must not be stored twice.
        var extractor = new StubExtractor(new ExtractedMemories(
            RepoMemories: new[] { "repository uses xunit" },
            TaskMemories: Array.Empty<string>()));
        var provider = BuildProvider(store, extractor);

        await InvokeStoreAsync(provider, new[] { User("q") }, new[] { Assistant("a") });

        var repo = await store.LoadRepoMemoriesAsync(RepoId, CancellationToken.None);
        repo.Should().ContainSingle().Which.Should().Be("Repository uses xUnit");
    }

    [Fact]
    public async Task StoreAIContext_caps_stored_memories_dropping_the_oldest()
    {
        var store = new InMemoryAgentMemoryStore();
        await store.SaveRepoMemoriesAsync(RepoId, new[] { "old1", "old2" }, CancellationToken.None);
        var extractor = new StubExtractor(new ExtractedMemories(
            RepoMemories: new[] { "new1", "new2" },
            TaskMemories: Array.Empty<string>()));
        var provider = BuildProvider(store, extractor, maxStoredPerScope: 3);

        await InvokeStoreAsync(provider, new[] { User("q") }, new[] { Assistant("a") });

        var repo = await store.LoadRepoMemoriesAsync(RepoId, CancellationToken.None);
        // old1 dropped to keep the list at the cap of 3, newest kept.
        repo.Should().Equal("old2", "new1", "new2");
    }

    [Fact]
    public async Task StoreAIContext_on_failed_run_does_not_extract_or_persist()
    {
        var store = new InMemoryAgentMemoryStore();
        var extractor = new StubExtractor(new ExtractedMemories(
            RepoMemories: new[] { "should-not-be-saved" }, TaskMemories: Array.Empty<string>()));
        var provider = BuildProvider(store, extractor);

        await InvokeStoreFailedAsync(provider, new[] { User("q") }, new InvalidOperationException("boom"));

        extractor.Invocations.Should().BeEmpty(
            because: "the agent must not learn from a run that threw");
        (await store.LoadRepoMemoriesAsync(RepoId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task StoreAIContext_with_nothing_new_does_not_write()
    {
        var store = new InMemoryAgentMemoryStore();
        await store.SaveRepoMemoriesAsync(RepoId, new[] { "existing" }, CancellationToken.None);
        var provider = BuildProvider(store, new StubExtractor(ExtractedMemories.Empty));

        await InvokeStoreAsync(provider, new[] { User("q") }, new[] { Assistant("a") });

        // Unchanged — and the dedup/no-op path left the single existing memory intact.
        (await store.LoadRepoMemoriesAsync(RepoId, CancellationToken.None)).Should().Equal("existing");
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    public void Ctor_rejects_non_positive_caps(int maxInjected, int maxStored)
    {
        var act = () => new DaprAgentMemoryContextProvider(
            new InMemoryAgentMemoryStore(), new StubExtractor(ExtractedMemories.Empty),
            RepoId, ProjectItemId, maxInjected, maxStored);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
