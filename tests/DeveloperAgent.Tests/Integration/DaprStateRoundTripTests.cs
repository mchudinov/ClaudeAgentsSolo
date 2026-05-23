using Dapr.Client;
using FluentAssertions;
using Xunit;

namespace DeveloperAgent.Tests.Integration;

/// <summary>
/// Integration test that round-trips a key/value through the Dapr state store
/// named <c>agent-state-store</c> (the Dapr component authored in Step-8) via a
/// real <see cref="DaprClient"/>. This is the acceptance criterion for the
/// final part of P2-A: an outside-process call lands in Redis and comes back
/// out unchanged through the Dapr sidecar.
/// <para>
/// Requires a running Dapr sidecar (with the <c>agent-state-store</c> component
/// loaded) reachable via the default <c>DAPR_HTTP_PORT</c>/<c>DAPR_GRPC_PORT</c>
/// or via Aspire's launch profile. Gated behind <c>DAPR_INTEGRATION=1</c> so
/// the default test run never tries to reach a sidecar.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class DaprStateRoundTripTests
{
    private const string EnvGate = "DAPR_INTEGRATION";

    /// <summary>
    /// Name of the Dapr state-store component registered in
    /// <c>src/AppHost/AppHost.cs</c> (Step-8). Must match the component name
    /// passed to <c>AddDaprStateStore(...)</c> — this is what
    /// <see cref="DaprClient"/> uses to address the store, NOT the Redis
    /// resource name (<c>agent-state</c>).
    /// </summary>
    private const string StateStoreName = "agent-state-store";

    [SkippableFact]
    public async Task Save_then_get_round_trips_value_through_agent_state_store()
    {
        var reason = EnvironmentSkip.ReasonIfMissing(EnvGate);
        Skip.If(reason is not null, reason ?? string.Empty);

        // Fail-fast budget: if the sidecar isn't reachable, surface the problem
        // quickly rather than hanging the whole test run.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // A unique key per test invocation so concurrent CI runs cannot
        // interfere with each other, and so a stale value from a previous run
        // can never falsely satisfy the assertion.
        var key = $"step-9-roundtrip-{Guid.NewGuid():N}";
        var expected = $"value-{Guid.NewGuid():N}";

        using var client = new DaprClientBuilder().Build();

        await client.SaveStateAsync(StateStoreName, key, expected, cancellationToken: cts.Token);

        var actual = await client.GetStateAsync<string>(StateStoreName, key, cancellationToken: cts.Token);

        actual.Should().Be(
            expected,
            because: "the value written via SaveStateAsync to '{0}' must come back unchanged " +
                     "through GetStateAsync — proves the Dapr component, sidecar, and Redis " +
                     "backing store are wired correctly.",
            StateStoreName);

        // Cleanup: best-effort delete so the Redis state store doesn't accumulate
        // garbage across CI runs. Failures here do not invalidate the test result.
        try
        {
            await client.DeleteStateAsync(StateStoreName, key, cancellationToken: cts.Token);
        }
        catch
        {
            // Intentionally swallowed: cleanup is opportunistic.
        }
    }
}
