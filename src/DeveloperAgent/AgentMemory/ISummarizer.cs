using Microsoft.Extensions.AI;

namespace DeveloperAgent.AgentMemory;

/// <summary>
/// Compacts a list of overflow chat messages into a single textual summary.
/// </summary>
/// <remarks>
/// <see cref="DaprChatHistoryProvider"/> calls this when the persisted non-system
/// message count exceeds the configured window. Step-20 (P2-G part 3/3) will land the
/// LLM-backed production implementation; Step-19 only defines the seam — tests inject a
/// deterministic stub so the windowing/compaction semantics are observable without
/// model traffic.
/// </remarks>
public interface ISummarizer
{
    ValueTask<string> SummarizeAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct);
}
