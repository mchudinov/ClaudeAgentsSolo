namespace ServiceDefaults.Resilience;

/// <summary>
/// Tunables for the in-process HTTP resilience pipeline applied to a named
/// HttpClient via <c>AddStandardResilienceHandler()</c>. Bound from the
/// <c>HttpResilience</c> section of <c>appsettings.json</c>.
/// </summary>
/// <remarks>
/// Only the per-attempt timeout is exposed today. The standard handler's shipped
/// default is 10 seconds, which cancels slow Anthropic streaming completions
/// (the SDK keeps one long-lived request open while tokens stream). The agent
/// default is raised to 30 seconds. <see cref="HttpResilienceConfigurator"/>
/// derives the dependent total-request and circuit-breaker windows from this
/// value so the package's startup validator stays satisfied.
/// </remarks>
public sealed record HttpResilienceOptions
{
    /// <summary>
    /// Per-attempt HTTP timeout, in seconds. Maps to
    /// <c>HttpStandardResilienceOptions.AttemptTimeout.Timeout</c> — the maximum
    /// duration of a single request attempt before the pipeline cancels it and
    /// (optionally) retries. Must be &gt; 0.
    /// </summary>
    public int AttemptTimeoutSeconds { get; init; } = 30;
}
