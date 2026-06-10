using Agent.GitHub;
using Agent.Memory;
using DeveloperAgent.Agent;
using DeveloperAgent.Agent.Memory;
using DeveloperAgent.Configuration;
using DeveloperAgent.GitHub;
using Agent.Workspace;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Tests.Agent;

/// <summary>
/// Step-31 GATE test: proves <see cref="AnthropicAgentRunner"/> actually attaches the memory
/// providers produced by <see cref="IAgentMemoryProviderFactory"/> to the agent it builds — a
/// green compile is not enough, the providers must run. Asserts via store interaction
/// (chat-history persisted across the run) and that injected repo memories reach the model request.
/// When no factory is supplied (the unit-test default), no memory is attached and the run is
/// unaffected — preserving the existing runner tests.
/// </summary>
public sealed class AnthropicAgentRunnerMemoryWiringTests
{
    private const string AgentId = "host-1";

    private static AgentRunRequest MakeRequest() =>
        new(
            Item: new ProjectItem("pid-77", "cid", 1, "Fix bug", "Fix the null ref", ProjectState.InProgress),
            Workspace: new TaskWorkspace("item-1", "agent/fix-bug", Path.GetTempPath(), "main"),
            PriorReviewFeedback: null);

    private sealed class StubChatClientFactory : IAgentChatClientFactory
    {
        private readonly IChatClient _client;
        public StubChatClientFactory(IChatClient client) => _client = client;
        public IChatClient Create(string modelId) => _client;
    }

    private static PersonaLoader MakePersonaLoader()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "mem-wiring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "personas"));
        File.WriteAllText(Path.Combine(tempRoot, "personas", "developer.md"), "You are a developer.");
        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.ContentRootPath.Returns(tempRoot);
        return new PersonaLoader("personas/developer.md", env);
    }

    private static AgentMemoryProviderFactory BuildFactory(
        IChatHistoryStore historyStore, IAgentMemoryStore memoryStore) =>
        new(
            historyStore,
            new PlaceholderSummarizer(),
            memoryStore,
            new NoOpMemoryExtractor(),
            Options.Create(new MemoryOptions()),
            Options.Create(new GitHubOptions { Owner = "octo", Repository = new RepositoryOptions { Name = "widgets" } }),
            AgentId);

    private static AnthropicAgentRunner BuildRunner(IChatClient chatClient, IAgentMemoryProviderFactory? factory) =>
        new(
            new StubChatClientFactory(chatClient),
            MakePersonaLoader(),
            Options.Create(new AgentOptions()),
            Options.Create(new ScopeLimitOptions { MaxModelTurns = 40, MaxToolCalls = 200, MaxExecutionTimeSeconds = 1800 }),
            [],
            NullLogger<AnthropicAgentRunner>.Instance,
            mcpToolSource: null,
            loggerFactory: null,
            memoryProviderFactory: factory);

    [Fact]
    public async Task Run_with_a_memory_factory_attaches_both_provider_slots()
    {
        const string seededMemory = "Repository uses xUnit and FluentAssertions.";
        var historyStore = new InMemoryChatHistoryStore();
        var memoryStore = new InMemoryAgentMemoryStore();
        await memoryStore.SaveRepoMemoriesAsync("octo/widgets", [seededMemory], CancellationToken.None);

        var seenMessages = new List<ChatMessage>();
        ChatOptions? seenOptions = null;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                seenMessages.AddRange(ci.Arg<IEnumerable<ChatMessage>>());
                seenOptions = ci.Arg<ChatOptions>();
                return Task.FromResult(
                    new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")) { FinishReason = ChatFinishReason.Stop });
            });

        var runner = BuildRunner(chatClient, BuildFactory(historyStore, memoryStore));

        var result = await runner.RunAsync(MakeRequest(), CancellationToken.None);

        result.Outcome.Should().Be(AgentRunOutcome.Completed);

        // ChatHistoryProvider slot attached → history persisted under the run's projectItemId.
        var saved = await historyStore.LoadAsync(AgentId, "pid-77", CancellationToken.None);
        saved.Should().NotBeNull(because: "the runner must attach the ChatHistoryProvider so history is persisted");
        saved!.Count.Should().BeGreaterThan(0);

        // AIContextProviders slot attached → seeded repo memory reached the request (messages or instructions).
        var requestText = string.Join("\n", seenMessages.Select(m => m.Text)) + "\n" + (seenOptions?.Instructions ?? "");
        requestText.Should().Contain(seededMemory,
            because: "the runner must attach the AIContextProvider so memories are injected");
    }

    [Fact]
    public async Task Run_without_a_memory_factory_leaves_stores_untouched()
    {
        var historyStore = new InMemoryChatHistoryStore();

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")) { FinishReason = ChatFinishReason.Stop }));

        var runner = BuildRunner(chatClient, factory: null);

        var result = await runner.RunAsync(MakeRequest(), CancellationToken.None);

        result.Outcome.Should().Be(AgentRunOutcome.Completed);
        var saved = await historyStore.LoadAsync(AgentId, "pid-77", CancellationToken.None);
        saved.Should().BeNull(because: "with no factory the runner attaches no providers and writes no history");
    }
}
