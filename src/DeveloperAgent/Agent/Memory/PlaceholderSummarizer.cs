using Agent.Memory;
using Microsoft.Extensions.AI;

namespace DeveloperAgent.Agent.Memory;

/// <summary>
/// Host-supplied placeholder body for the <see cref="ISummarizer"/> seam (Step-31 wiring).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DaprChatHistoryProvider"/> calls this only when persisted history overflows the
/// configured window; the result replaces the older turns with one summary system message, keeping
/// the chat history bounded. This placeholder produces a deterministic, model-free marker so the
/// windowing/compaction pipeline is fully functional without any Anthropic traffic.
/// </para>
/// <para>
/// The LLM-backed body (ask the model to summarise the overflow) is deferred — see
/// <c>docs/plans/07-phase-2-outline.md</c> §P2-G and the seam doc on <see cref="ISummarizer"/>.
/// Swapping this registration for an LLM-backed implementation is the only change needed later.
/// </para>
/// </remarks>
public sealed class PlaceholderSummarizer : ISummarizer
{
    public ValueTask<string> SummarizeAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return ValueTask.FromResult(
            $"{messages.Count} earlier message(s) were compacted to keep the context window bounded. " +
            "(Detailed summarisation is not yet enabled.)");
    }
}
