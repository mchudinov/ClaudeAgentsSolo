using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace DeveloperAgent.AgentMemory;

/// <summary>
/// Microsoft Agent Framework <see cref="AIContextProvider"/> (LLD memory layer 3) backed by an
/// <see cref="IAgentMemoryStore"/> (Dapr in production). It injects the agent's accumulated
/// repository conventions and per-task lessons before each model call and distils useful new
/// memories from each completed run via <see cref="IMemoryExtractor"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Before the call</b> (<see cref="ProvideAIContextAsync"/>): load repo memories
/// (<c>repo-state:{repoId}</c>) and task memories (<c>task-memory:{projectItemId}</c>) and fold
/// the most recent <see cref="_maxInjectedPerScope"/> of each into a single
/// <see cref="AIContext.Instructions"/> block. Returns an empty (non-null) context when there is
/// nothing to inject.
/// </para>
/// <para>
/// <b>After the call</b> (<see cref="StoreAIContextAsync"/>): runs the extractor over the run's
/// request+response messages and appends any genuinely new (case-insensitively de-duplicated
/// against what is already persisted) memories to each namespace, capping each stored list at
/// <see cref="_maxStoredPerScope"/> by dropping the oldest. Runs that threw
/// (<see cref="AIContextProvider.InvokedContext.InvokeException"/> set) are skipped — the agent
/// does not learn from a failed turn.
/// </para>
/// <para>
/// Like <see cref="DaprChatHistoryProvider"/>, the store is the storage of record, so
/// <see cref="StateKeys"/> claims no <see cref="AgentSession"/> state-bag keys.
/// </para>
/// </remarks>
public sealed class DaprAgentMemoryContextProvider : AIContextProvider
{
    /// <summary>Header line that precedes the injected memory bullets in <see cref="AIContext.Instructions"/>.</summary>
    public const string InstructionsHeader = "Relevant memories from earlier work:";

    private static readonly IReadOnlyList<string> EmptyStateKeys = Array.Empty<string>();

    private static IEnumerable<ChatMessage> PassThroughFilter(IEnumerable<ChatMessage> messages) => messages;

    private readonly IAgentMemoryStore _store;
    private readonly IMemoryExtractor _extractor;
    private readonly string _repoId;
    private readonly string _projectItemId;
    private readonly int _maxInjectedPerScope;
    private readonly int _maxStoredPerScope;

    public DaprAgentMemoryContextProvider(
        IAgentMemoryStore store,
        IMemoryExtractor extractor,
        string repoId,
        string projectItemId,
        int maxInjectedPerScope,
        int maxStoredPerScope)
        : base(PassThroughFilter, PassThroughFilter, PassThroughFilter)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentException.ThrowIfNullOrEmpty(repoId);
        ArgumentException.ThrowIfNullOrEmpty(projectItemId);
        if (maxInjectedPerScope <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxInjectedPerScope), maxInjectedPerScope,
                "maxInjectedPerScope must be > 0");
        if (maxStoredPerScope <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxStoredPerScope), maxStoredPerScope,
                "maxStoredPerScope must be > 0");

        _store = store;
        _extractor = extractor;
        _repoId = repoId;
        _projectItemId = projectItemId;
        _maxInjectedPerScope = maxInjectedPerScope;
        _maxStoredPerScope = maxStoredPerScope;
    }

    public override IReadOnlyList<string> StateKeys => EmptyStateKeys;

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var repo = await _store.LoadRepoMemoriesAsync(_repoId, cancellationToken).ConfigureAwait(false);
        var task = await _store.LoadTaskMemoriesAsync(_projectItemId, cancellationToken).ConfigureAwait(false);

        // Inject only the most-recent slice of each namespace so the block stays bounded —
        // the whole point of the memory layer is to feed the model durable context without
        // letting it grow without limit (LLD §memory design).
        var repoSlice = MostRecent(repo, _maxInjectedPerScope);
        var taskSlice = MostRecent(task, _maxInjectedPerScope);

        if (repoSlice.Count == 0 && taskSlice.Count == 0)
            return new AIContext();

        var sb = new System.Text.StringBuilder();
        sb.Append(InstructionsHeader);
        AppendSection(sb, "Repository conventions:", repoSlice);
        AppendSection(sb, "Lessons from this task:", taskSlice);

        return new AIContext { Instructions = sb.ToString() };
    }

    protected override async ValueTask StoreAIContextAsync(
        InvokedContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Never learn from a run that threw — the conversation it produced is unreliable.
        if (context.InvokeException is not null)
            return;

        var conversation = new List<ChatMessage>();
        if (context.RequestMessages is not null) conversation.AddRange(context.RequestMessages);
        if (context.ResponseMessages is not null) conversation.AddRange(context.ResponseMessages);

        var extracted = await _extractor.ExtractAsync(conversation, cancellationToken).ConfigureAwait(false);

        await MergeAndSaveAsync(
            extracted.RepoMemories,
            () => _store.LoadRepoMemoriesAsync(_repoId, cancellationToken),
            memories => _store.SaveRepoMemoriesAsync(_repoId, memories, cancellationToken))
            .ConfigureAwait(false);

        await MergeAndSaveAsync(
            extracted.TaskMemories,
            () => _store.LoadTaskMemoriesAsync(_projectItemId, cancellationToken),
            memories => _store.SaveTaskMemoriesAsync(_projectItemId, memories, cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Appends any genuinely new (case-insensitively de-duplicated against what is already
    /// stored) entries from <paramref name="incoming"/>, caps the result at
    /// <see cref="_maxStoredPerScope"/> by dropping the oldest, and persists — but only when
    /// something actually changed, so a no-op extraction does not churn the store.
    /// </summary>
    private async Task MergeAndSaveAsync(
        IReadOnlyList<string> incoming,
        Func<Task<IReadOnlyList<string>?>> load,
        Func<IReadOnlyList<string>, Task> save)
    {
        if (incoming is null || incoming.Count == 0)
            return;

        var existing = await load().ConfigureAwait(false);
        var merged = existing is null ? new List<string>() : new List<string>(existing);

        var seen = new HashSet<string>(merged, StringComparer.OrdinalIgnoreCase);
        var added = false;
        foreach (var memory in incoming)
        {
            if (string.IsNullOrWhiteSpace(memory)) continue;
            var trimmed = memory.Trim();
            if (seen.Add(trimmed))
            {
                merged.Add(trimmed);
                added = true;
            }
        }

        if (!added)
            return;

        // Cap by dropping the oldest entries (front of the list).
        if (merged.Count > _maxStoredPerScope)
            merged.RemoveRange(0, merged.Count - _maxStoredPerScope);

        await save(merged).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> MostRecent(IReadOnlyList<string>? memories, int max)
    {
        if (memories is null || memories.Count == 0)
            return Array.Empty<string>();
        if (memories.Count <= max)
            return memories;
        return memories.Skip(memories.Count - max).ToList();
    }

    private static void AppendSection(System.Text.StringBuilder sb, string label, IReadOnlyList<string> memories)
    {
        if (memories.Count == 0)
            return;
        sb.Append('\n').Append(label);
        foreach (var memory in memories)
            sb.Append("\n- ").Append(memory);
    }
}
