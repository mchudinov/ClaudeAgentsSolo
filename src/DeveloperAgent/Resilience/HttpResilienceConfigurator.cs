using Microsoft.Extensions.Http.Resilience;

namespace DeveloperAgent.Resilience;

/// <summary>
/// Translates <see cref="HttpResilienceOptions"/> into a
/// <see cref="HttpStandardResilienceOptions"/> mutation applied inside each
/// <c>AddStandardResilienceHandler(...)</c> registration.
/// </summary>
/// <remarks>
/// The standard handler validates its own options on start (<c>ValidateOnStart</c>),
/// and two of its rules couple the per-attempt timeout to other windows:
/// <list type="bullet">
/// <item><c>TotalRequestTimeout</c> must be strictly greater than
/// <c>AttemptTimeout</c> (the whole pipeline, retries included, must outlast one
/// attempt).</item>
/// <item><c>CircuitBreaker.SamplingDuration</c> must be at least 2× the
/// <c>AttemptTimeout</c>.</item>
/// </list>
/// Raising only the attempt timeout would therefore break startup. This helper
/// lifts the two dependent windows just enough to keep them valid, leaving the
/// shipped defaults untouched whenever they already clear the new floors.
/// </remarks>
public static class HttpResilienceConfigurator
{
    public static void Apply(HttpStandardResilienceOptions resilience, HttpResilienceOptions options)
    {
        ArgumentNullException.ThrowIfNull(resilience);
        ArgumentNullException.ThrowIfNull(options);

        var attempt = TimeSpan.FromSeconds(options.AttemptTimeoutSeconds);
        resilience.AttemptTimeout.Timeout = attempt;

        // The total-request timeout bounds the entire pipeline (all retries). Give
        // retries room — at least 3× one attempt — but never shrink a larger default.
        var minTotal = TimeSpan.FromTicks(attempt.Ticks * 3);
        if (resilience.TotalRequestTimeout.Timeout < minTotal)
        {
            resilience.TotalRequestTimeout.Timeout = minTotal;
        }

        // The circuit-breaker sampling window must be ≥ 2× the attempt timeout.
        var minSampling = TimeSpan.FromTicks(attempt.Ticks * 2);
        if (resilience.CircuitBreaker.SamplingDuration < minSampling)
        {
            resilience.CircuitBreaker.SamplingDuration = minSampling;
        }
    }
}
