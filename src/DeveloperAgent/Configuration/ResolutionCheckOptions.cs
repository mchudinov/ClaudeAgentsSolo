namespace DeveloperAgent.Configuration;

/// <summary>
/// Already-resolved gate policy, bound from the <c>ResolutionCheck</c> section. The gate runs after the
/// branch/workspace is prepared but before the agent plans/implements, and decides whether the work the
/// item describes is already implemented in the real working tree. When it is, the item is commented and
/// parked in the write-only Backlog column instead of being re-implemented.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Enabled"/> defaults to <see langword="false"/> so a host without a <c>ResolutionCheck</c>
/// section boots with no behaviour change and no surprise LLM calls. Mirrors the default-off pattern of
/// <see cref="TriageOptions"/>. The check's <em>judgment quality</em> depends on prompt tuning against
/// live runs, so it is opt-in.
/// </para>
/// <para>
/// The single member is a scalar, so seeding a default here is safe — the <c>ConfigurationBinder</c>
/// append-on-default gotcha applies only to list/array properties.
/// </para>
/// </remarks>
public sealed record ResolutionCheckOptions
{
    /// <summary>When <see langword="false"/>, the already-resolved gate is a no-op and every item proceeds.</summary>
    public bool Enabled { get; init; } = false;
}
