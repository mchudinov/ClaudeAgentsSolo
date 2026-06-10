using Microsoft.Agents.AI;

namespace DeveloperAgent.Agent.Memory;

/// <summary>
/// The per-run MAF memory providers the runner attaches to a <c>ChatClientAgentOptions</c>.
/// </summary>
/// <remarks>
/// MAF exposes two distinct slots — a single <see cref="Microsoft.Agents.AI.ChatHistoryProvider"/>
/// (<c>ChatClientAgentOptions.ChatHistoryProvider</c>) and a list of
/// <see cref="Microsoft.Agents.AI.AIContextProvider"/> (<c>ChatClientAgentOptions.AIContextProviders</c>).
/// <c>ChatHistoryProvider</c> is NOT an <c>AIContextProvider</c>, so the two cannot share a list;
/// this record keeps them separate and lets <see cref="Empty"/> represent "memory disabled".
/// </remarks>
public sealed record AgentMemoryProviders(
    ChatHistoryProvider? ChatHistory,
    IReadOnlyList<AIContextProvider> ContextProviders)
{
    /// <summary>No providers — used when memory is disabled. The runner attaches nothing.</summary>
    public static AgentMemoryProviders Empty { get; } =
        new(null, Array.Empty<AIContextProvider>());
}
