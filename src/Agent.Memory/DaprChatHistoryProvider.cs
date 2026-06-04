using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agent.Memory;

/// <summary>
/// Microsoft Agent Framework <see cref="ChatHistoryProvider"/> backed by an
/// <see cref="IChatHistoryStore"/> (Dapr in production) with a rolling-window
/// compaction policy: when the persisted non-system message count exceeds
/// <see cref="_maxRecentTurns"/>, the older overflow is replaced by a single summary
/// system message produced by <see cref="ISummarizer"/>.
/// </summary>
/// <remarks>
/// <para>
/// State layout: the full message list is persisted directly via
/// <see cref="IChatHistoryStore"/> (key <c>chat-history:{agentId}:{projectItemId}</c>).
/// <see cref="StateKeys"/> returns an empty list because this provider does **not**
/// piggyback on <see cref="AgentSession.StateBag"/> — Dapr is the storage of record.
/// </para>
/// <para>
/// Windowing policy (applied inside <see cref="StoreChatHistoryAsync"/>):
/// <list type="number">
/// <item>Load the prior persisted list.</item>
/// <item>Drop any previously-produced summary message (recognised by the
///   <see cref="SummaryPrefix"/> marker) so it never feeds the next summarizer call —
///   the summarizer is always given raw turns, never a summary-of-a-summary.</item>
/// <item>Append the new request and response messages.</item>
/// <item>Preserve any contiguous run of <see cref="ChatRole.System"/> messages at the head;
///   everything after that is windowable.</item>
/// <item>If the windowable count exceeds <see cref="_maxRecentTurns"/>, summarise the
///   overflow and prepend a single summary system message in front of the most-recent
///   <see cref="_maxRecentTurns"/> turns.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class DaprChatHistoryProvider : ChatHistoryProvider
{
    /// <summary>
    /// Marker that prefixes the text of any summary system message this provider
    /// produces. Used both to format new summaries and to recognise (and discard)
    /// prior summaries when re-computing during repeated overflow.
    /// </summary>
    public const string SummaryPrefix = "[Summary of earlier conversation]\n";

    private static readonly IReadOnlyList<string> EmptyStateKeys = Array.Empty<string>();

    /// <summary>
    /// Identity filter used for all three required base-class filter slots. We do our
    /// own windowing/compaction inside <see cref="StoreChatHistoryAsync"/>, so no
    /// pre-filtering of the request/response stream is desired.
    /// </summary>
    private static IEnumerable<ChatMessage> PassThroughFilter(IEnumerable<ChatMessage> messages) => messages;

    private readonly IChatHistoryStore _store;
    private readonly ISummarizer _summarizer;
    private readonly string _agentId;
    private readonly string _projectItemId;
    private readonly int _maxRecentTurns;

    public DaprChatHistoryProvider(
        IChatHistoryStore store,
        ISummarizer summarizer,
        string agentId,
        string projectItemId,
        int maxRecentTurns)
        : base(
            PassThroughFilter,
            PassThroughFilter,
            PassThroughFilter)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(summarizer);
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        ArgumentException.ThrowIfNullOrEmpty(projectItemId);
        if (maxRecentTurns <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRecentTurns), maxRecentTurns,
                "maxRecentTurns must be > 0");

        _store = store;
        _summarizer = summarizer;
        _agentId = agentId;
        _projectItemId = projectItemId;
        _maxRecentTurns = maxRecentTurns;
    }

    /// <summary>
    /// We persist chat history via <see cref="IChatHistoryStore"/> (Dapr), not via the
    /// MAF <see cref="AgentSessionStateBag"/>, so no state-bag keys are claimed.
    /// </summary>
    public override IReadOnlyList<string> StateKeys => EmptyStateKeys;

    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var persisted = await _store.LoadAsync(_agentId, _projectItemId, cancellationToken)
            .ConfigureAwait(false);
        return persisted ?? (IReadOnlyList<ChatMessage>)Array.Empty<ChatMessage>();
    }

    protected override async ValueTask StoreChatHistoryAsync(
        InvokedContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var persisted = await _store.LoadAsync(_agentId, _projectItemId, cancellationToken)
            .ConfigureAwait(false);

        // Strip any prior summary message from the persisted list. We never re-feed our
        // own previous summary back into the summarizer — repeated overflow always
        // re-summarises raw turns from whatever overflow currently exists.
        var combined = new List<ChatMessage>(capacity: (persisted?.Count ?? 0) + 8);
        if (persisted is not null)
        {
            foreach (var m in persisted)
            {
                if (IsSummaryMessage(m)) continue;
                combined.Add(m);
            }
        }

        if (context.RequestMessages is not null)
            combined.AddRange(context.RequestMessages);
        if (context.ResponseMessages is not null)
            combined.AddRange(context.ResponseMessages);

        // Find the leading run of system messages — those are always preserved at the
        // head of the persisted list. Everything after that is windowable.
        var leadingSystemCount = 0;
        while (leadingSystemCount < combined.Count
               && combined[leadingSystemCount].Role == ChatRole.System)
        {
            leadingSystemCount++;
        }

        var windowableCount = combined.Count - leadingSystemCount;
        if (windowableCount <= _maxRecentTurns)
        {
            // Below window — persist verbatim, no summarizer call.
            await _store.SaveAsync(_agentId, _projectItemId, combined, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // Above window — split into overflow (older) + kept (newest _maxRecentTurns).
        var overflowCount = windowableCount - _maxRecentTurns;
        var overflow = combined.GetRange(leadingSystemCount, overflowCount);
        var kept = combined.GetRange(leadingSystemCount + overflowCount, _maxRecentTurns);

        var summaryText = await _summarizer.SummarizeAsync(overflow, cancellationToken)
            .ConfigureAwait(false);
        var summaryMessage = new ChatMessage(ChatRole.System, SummaryPrefix + summaryText);

        var rebuilt = new List<ChatMessage>(capacity: leadingSystemCount + 1 + kept.Count);
        for (var i = 0; i < leadingSystemCount; i++) rebuilt.Add(combined[i]);
        rebuilt.Add(summaryMessage);
        rebuilt.AddRange(kept);

        await _store.SaveAsync(_agentId, _projectItemId, rebuilt, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="message"/> is a summary
    /// system message produced by this provider (recognised by the
    /// <see cref="SummaryPrefix"/> marker on its <see cref="ChatMessage.Text"/>).
    /// </summary>
    private static bool IsSummaryMessage(ChatMessage message)
    {
        if (message.Role != ChatRole.System) return false;
        var text = message.Text;
        return !string.IsNullOrEmpty(text) && text.StartsWith(SummaryPrefix, StringComparison.Ordinal);
    }
}
