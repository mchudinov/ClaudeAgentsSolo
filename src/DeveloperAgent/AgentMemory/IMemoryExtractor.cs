using Microsoft.Extensions.AI;

namespace DeveloperAgent.AgentMemory;

/// <summary>
/// New durable memories distilled from a single completed agent run, split into the two
/// namespaces <see cref="DaprAgentMemoryContextProvider"/> persists.
/// </summary>
/// <param name="RepoMemories">Repository-wide conventions worth remembering for every future
/// task on the repo (e.g. "Repository uses xUnit"). Persisted under <c>repo-state:{repoId}</c>.</param>
/// <param name="TaskMemories">Lessons scoped to the current GitHub project item. Persisted
/// under <c>task-memory:{projectItemId}</c>.</param>
public sealed record ExtractedMemories(
    IReadOnlyList<string> RepoMemories,
    IReadOnlyList<string> TaskMemories)
{
    /// <summary>An extraction that produced nothing new.</summary>
    public static ExtractedMemories Empty { get; } =
        new(Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>
/// Distils useful new memories from a completed run's conversation. Called by
/// <see cref="DaprAgentMemoryContextProvider.StoreAIContextAsync"/> after each model
/// invocation.
/// </summary>
/// <remarks>
/// This is the seam that Step-20 lands; the LLM-backed production implementation (asking the
/// model "what should I remember about this repo / task?") is deferred to a later step, the
/// same way <see cref="ISummarizer"/> defers its model-backed body. Tests inject a
/// deterministic stub so the inject/extract/persist semantics are observable without model
/// traffic.
/// </remarks>
public interface IMemoryExtractor
{
    ValueTask<ExtractedMemories> ExtractAsync(IReadOnlyList<ChatMessage> conversation, CancellationToken ct);
}
