namespace DeveloperAgent.Configuration;

/// <summary>
/// Relevance-triage policy, bound from the <c>Triage</c> section. The triage gate runs before the
/// agent acquires/branches/plans an item and decides whether the item is worth working: it must be
/// both in-scope for the project (<see cref="RepoScope"/>) and within the agent's skill
/// (<see cref="AgentSkill"/>). Rejected items are parked in the write-only Backlog column.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Enabled"/> defaults to <see langword="false"/> so a host without a <c>Triage</c> config
/// section boots with no behaviour change and no surprise LLM calls; the shipped <c>appsettings.json</c>
/// sets it to <see langword="true"/>. Mirrors the default-off pattern of <c>ContainerRuntime</c> and the
/// MCP servers.
/// </para>
/// <para>
/// All three members are scalars, so seeding defaults here is safe — the <c>ConfigurationBinder</c>
/// append-on-default gotcha applies only to list/array properties.
/// </para>
/// </remarks>
public sealed record TriageOptions
{
    /// <summary>When <see langword="false"/>, the triage gate is a no-op and every item proceeds.</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// Plain-language description of what this project/repository is about and what work belongs to it.
    /// Required (non-empty) when <see cref="Enabled"/> is <see langword="true"/> — enforced at startup.
    /// </summary>
    public string RepoScope { get; init; } = "";

    /// <summary>Plain-language description of what this agent is capable of implementing.</summary>
    public string AgentSkill { get; init; } =
        "Implementing C#/.NET coding tasks — features, bug fixes, refactors, and automated tests — " +
        "and delivering them as a pull request.";
}
