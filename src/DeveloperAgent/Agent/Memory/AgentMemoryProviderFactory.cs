using Agent.GitHub;
using Agent.Memory;
using DeveloperAgent.Configuration;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Agent.Memory;

/// <summary>
/// Builds the two per-run MAF memory providers (LLD §P2-G) for a GitHub project item:
/// <see cref="DaprChatHistoryProvider"/> (rolling-window chat history) and
/// <see cref="DaprAgentMemoryContextProvider"/> (repo-convention + task-lesson injection).
/// </summary>
/// <remarks>
/// <para>
/// The <c>agentId</c> is supplied by the host (typically <see cref="Environment.MachineName"/>) and
/// MUST match the value passed to <c>AddAgentMemoryServices</c>, so chat history shares the session
/// store's <c>{agentId}</c> namespace. The <c>repoId</c> is derived as <c>{Owner}/{Repository.Name}</c>
/// from <see cref="GitHubOptions"/>, matching the <c>repo-state:{repoId}</c> key the memory store uses.
/// </para>
/// <para>
/// When <see cref="MemoryOptions.Enabled"/> is <see langword="false"/> this returns
/// <see cref="AgentMemoryProviders.Empty"/> and the agent runs with no memory layer (the pre-Step-31
/// behaviour) — a kill-switch for environments without a Dapr state store.
/// </para>
/// </remarks>
public sealed class AgentMemoryProviderFactory : IAgentMemoryProviderFactory
{
    private readonly IChatHistoryStore _chatHistoryStore;
    private readonly ISummarizer _summarizer;
    private readonly IAgentMemoryStore _memoryStore;
    private readonly IMemoryExtractor _extractor;
    private readonly MemoryOptions _options;
    private readonly string _agentId;
    private readonly string _repoId;

    public AgentMemoryProviderFactory(
        IChatHistoryStore chatHistoryStore,
        ISummarizer summarizer,
        IAgentMemoryStore memoryStore,
        IMemoryExtractor extractor,
        IOptions<MemoryOptions> options,
        IOptions<GitHubOptions> gitHubOptions,
        string agentId)
    {
        ArgumentNullException.ThrowIfNull(chatHistoryStore);
        ArgumentNullException.ThrowIfNull(summarizer);
        ArgumentNullException.ThrowIfNull(memoryStore);
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(gitHubOptions);
        ArgumentException.ThrowIfNullOrEmpty(agentId);

        _chatHistoryStore = chatHistoryStore;
        _summarizer = summarizer;
        _memoryStore = memoryStore;
        _extractor = extractor;
        _options = options.Value;
        _agentId = agentId;

        var gh = gitHubOptions.Value;
        _repoId = $"{gh.Owner}/{gh.Repository.Name}";
    }

    public AgentMemoryProviders Create(string projectItemId)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectItemId);

        if (!_options.Enabled)
            return AgentMemoryProviders.Empty;

        var chatHistory = new DaprChatHistoryProvider(
            _chatHistoryStore, _summarizer, _agentId, projectItemId, _options.MaxRecentTurns);

        var memoryContext = new DaprAgentMemoryContextProvider(
            _memoryStore, _extractor, _repoId, projectItemId,
            _options.MaxInjectedPerScope, _options.MaxStoredPerScope);

        return new AgentMemoryProviders(chatHistory, [memoryContext]);
    }
}
