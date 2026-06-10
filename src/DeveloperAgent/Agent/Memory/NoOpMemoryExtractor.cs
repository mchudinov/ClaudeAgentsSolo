using Agent.Memory;
using Microsoft.Extensions.AI;

namespace DeveloperAgent.Agent.Memory;

/// <summary>
/// Host-supplied placeholder body for the <see cref="IMemoryExtractor"/> seam (Step-31 wiring).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DaprAgentMemoryContextProvider"/> calls this after each successful run to distil new
/// durable memories from the conversation. This placeholder returns <see cref="ExtractedMemories.Empty"/>
/// so no memories are <em>learned</em> automatically yet — but the provider's <b>inject</b> path is
/// fully live: repo conventions and the per-task summary that <c>CompactMemoryActivity</c> already
/// writes under <c>task-memory:{projectItemId}</c> are loaded and injected before each model call.
/// </para>
/// <para>
/// The LLM-backed body (ask the model "what should I remember about this repo / task?") is deferred —
/// see <c>docs/plans/07-phase-2-outline.md</c> §P2-G and the seam doc on <see cref="IMemoryExtractor"/>.
/// </para>
/// </remarks>
public sealed class NoOpMemoryExtractor : IMemoryExtractor
{
    public ValueTask<ExtractedMemories> ExtractAsync(IReadOnlyList<ChatMessage> conversation, CancellationToken ct)
        => ValueTask.FromResult(ExtractedMemories.Empty);
}
