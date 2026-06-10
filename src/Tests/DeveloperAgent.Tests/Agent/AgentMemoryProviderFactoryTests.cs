using Agent.GitHub;
using Agent.Memory;
using DeveloperAgent.Agent.Memory;
using DeveloperAgent.Configuration;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Tests.Agent;

/// <summary>
/// Step-31: the host-owned factory constructs the two per-run MAF memory providers with runtime
/// ids (agentId, repoId, projectItemId) and the configured window sizes, or returns
/// <see cref="AgentMemoryProviders.Empty"/> when memory is disabled. These tests pin both the
/// enabled/disabled branches and that the produced providers are wired to the injected stores
/// under the right keys.
/// </summary>
public sealed class AgentMemoryProviderFactoryTests
{
    private const string AgentId = "host-1";
    private const string Owner = "octo";
    private const string Repo = "widgets";
    private const string ProjectItemId = "PVTI_item9";
    private static string RepoId => $"{Owner}/{Repo}";

    private static IOptions<GitHubOptions> GitHub() =>
        Options.Create(new GitHubOptions { Owner = Owner, Repository = new RepositoryOptions { Name = Repo } });

    private static AgentMemoryProviderFactory Build(
        IChatHistoryStore historyStore,
        IAgentMemoryStore memoryStore,
        MemoryOptions options) =>
        new(
            historyStore,
            new PlaceholderSummarizer(),
            memoryStore,
            new NoOpMemoryExtractor(),
            Options.Create(options),
            GitHub(),
            AgentId);

    [Fact]
    public void Create_when_enabled_produces_a_history_provider_and_one_context_provider()
    {
        var factory = Build(new InMemoryChatHistoryStore(), new InMemoryAgentMemoryStore(), new MemoryOptions());

        var providers = factory.Create(ProjectItemId);

        providers.ChatHistory.Should().NotBeNull().And.BeOfType<DaprChatHistoryProvider>();
        providers.ContextProviders.Should().ContainSingle()
            .Which.Should().BeOfType<DaprAgentMemoryContextProvider>();
    }

    [Fact]
    public void Create_when_disabled_returns_Empty()
    {
        var factory = Build(
            new InMemoryChatHistoryStore(), new InMemoryAgentMemoryStore(),
            new MemoryOptions { Enabled = false });

        var providers = factory.Create(ProjectItemId);

        providers.ChatHistory.Should().BeNull();
        providers.ContextProviders.Should().BeEmpty();
    }

#pragma warning disable MAAI001
    [Fact]
    public async Task Produced_history_provider_persists_under_the_configured_agentId_and_projectItemId()
    {
        var store = new InMemoryChatHistoryStore();
        var factory = Build(store, new InMemoryAgentMemoryStore(), new MemoryOptions());

        var providers = factory.Create(ProjectItemId);

        var agent = Substitute.For<AIAgent>();
        var session = Substitute.For<Microsoft.Agents.AI.AgentSession>();
        var ctx = new ChatHistoryProvider.InvokedContext(
            agent, session, [new ChatMessage(ChatRole.User, "hi")], [new ChatMessage(ChatRole.Assistant, "yo")]);
        await providers.ChatHistory!.InvokedAsync(ctx, CancellationToken.None);

        var saved = await store.LoadAsync(AgentId, ProjectItemId, CancellationToken.None);
        saved.Should().NotBeNull(because: "the provider must persist under chat-history:{agentId}:{projectItemId}");
        saved!.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Produced_context_provider_injects_repo_memories_for_the_configured_repoId()
    {
        const string seeded = "Repository uses xUnit and FluentAssertions.";
        var memoryStore = new InMemoryAgentMemoryStore();
        await memoryStore.SaveRepoMemoriesAsync(RepoId, [seeded], CancellationToken.None);

        var factory = Build(new InMemoryChatHistoryStore(), memoryStore, new MemoryOptions());
        var providers = factory.Create(ProjectItemId);

        var agent = Substitute.For<AIAgent>();
        var session = Substitute.For<Microsoft.Agents.AI.AgentSession>();
        var ctx = new AIContextProvider.InvokingContext(agent, session, new AIContext());
        var result = await providers.ContextProviders[0].InvokingAsync(ctx, CancellationToken.None);

        result.Instructions.Should().Contain(seeded,
            because: "the context provider must load repo memories for repo-state:{repoId}");
    }
#pragma warning restore MAAI001
}
