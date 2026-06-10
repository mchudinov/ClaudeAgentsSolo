namespace DeveloperAgent.Agent.Memory;

/// <summary>
/// Host-policy factory that builds the per-run MAF memory providers for a given GitHub project
/// item. Lives in the host (not <c>Agent.Memory</c>) because it owns the developer-agent ids and
/// window policy — CLAUDE.md: "the providers/summarizer/extractor are constructed per run with
/// runtime ids, not via DI."
/// </summary>
public interface IAgentMemoryProviderFactory
{
    /// <summary>
    /// Builds the chat-history + memory-context providers scoped to <paramref name="projectItemId"/>,
    /// or <see cref="AgentMemoryProviders.Empty"/> when memory is disabled.
    /// </summary>
    AgentMemoryProviders Create(string projectItemId);
}
