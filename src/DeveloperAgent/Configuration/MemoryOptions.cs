namespace DeveloperAgent.Configuration;

/// <summary>
/// Host policy for the agent memory layer (LLD §P2-G). Controls whether the per-run MAF memory
/// providers (<c>DaprChatHistoryProvider</c> + <c>DaprAgentMemoryContextProvider</c>) are attached
/// to the agent and the window sizes that keep the injected context bounded. Bound from the
/// <c>Memory</c> configuration section.
/// </summary>
/// <remarks>
/// All four properties are scalars, so the <c>ConfigurationBinder</c> array-append gotcha (which
/// affects list/array defaults — see CLAUDE.md) does not apply; the defaults below are safe to seed.
/// </remarks>
public sealed record MemoryOptions
{
    /// <summary>
    /// Master switch. When <see langword="false"/> the runner attaches no memory providers and the
    /// agent runs exactly as it did before Step-31 (a kill-switch for environments without a Dapr
    /// state store, e.g. a bare <c>dotnet run</c>).
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Most-recent non-system turns the chat-history provider keeps verbatim before summarising the
    /// overflow into a single system message. Must be &gt; 0.
    /// </summary>
    public int MaxRecentTurns { get; init; } = 20;

    /// <summary>
    /// Maximum memories injected per namespace (repo conventions, task lessons) before each model
    /// call. Must be &gt; 0.
    /// </summary>
    public int MaxInjectedPerScope { get; init; } = 10;

    /// <summary>
    /// Maximum memories persisted per namespace; the oldest are dropped past this cap. Must be &gt; 0.
    /// </summary>
    public int MaxStoredPerScope { get; init; } = 50;
}
