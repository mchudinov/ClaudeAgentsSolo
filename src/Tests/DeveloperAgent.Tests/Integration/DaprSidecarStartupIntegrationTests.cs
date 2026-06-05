using System.Text;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeveloperAgent.Tests.Integration;

/// <summary>
/// End-to-end test that boots the Aspire AppHost with its real Dapr sidecar(s)
/// and asserts that the sidecar log stream reports the <c>agent-state-store</c>
/// component being loaded. This is the acceptance criterion for Step-8.
/// <para>
/// Requires Docker (for Redis) and the Dapr CLI to be installed on the host —
/// gated behind <c>RUN_DAPR_INTEGRATION</c> so the default test run never tries
/// to spin up a sidecar.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class DaprSidecarStartupIntegrationTests
{
    private const string EnvGate = "RUN_DAPR_INTEGRATION";

    [SkippableFact]
    public async Task Sidecar_startup_logs_announce_agent_state_store_component()
    {
        var reason = EnvironmentSkip.ReasonIfMissing(EnvGate);
        Skip.If(reason is not null, reason ?? string.Empty);

        // Fail-fast budget: if Docker isn't running or the sidecar can't start,
        // we don't want the test to hang for minutes — surface the problem fast.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.AppHost>(cts.Token);

        await using var app = await builder.BuildAsync(cts.Token);
        await app.StartAsync(cts.Token);

        var loggerService = app.Services.GetRequiredService<ResourceLoggerService>();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // The toolkit names its Dapr sidecar resource(s) without a fixed convention
        // we want to pin in this test. Stream logs from every resource and look for
        // the substring announcing the state-store component — the exact log line
        // format depends on the Dapr daprd version.
        var collected = new StringBuilder();
        var found = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamTasks = new List<Task>();

        foreach (var resource in model.Resources)
        {
            streamTasks.Add(StreamResourceLogsAsync(loggerService, resource, collected, found, cts.Token));
        }

        // Wait for either the substring to appear or the timeout to expire.
        using var registration = cts.Token.Register(() => found.TrySetResult(false));
        var matched = await found.Task.ConfigureAwait(false);

        await app.StopAsync(CancellationToken.None);

        matched.Should().BeTrue(
            because: "the Dapr sidecar must log a line containing 'agent-state-store' " +
                     "during startup to prove the component is registered. Collected logs:\n{0}",
            collected.ToString());
    }

    private static async Task StreamResourceLogsAsync(
        ResourceLoggerService loggerService,
        IResource resource,
        StringBuilder collected,
        TaskCompletionSource<bool> found,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var batch in loggerService.WatchAsync(resource).WithCancellation(cancellationToken))
            {
                foreach (var line in batch)
                {
                    lock (collected)
                    {
                        collected.AppendLine($"[{resource.Name}] {line.Content}");
                    }

                    if (line.Content.Contains("agent-state-store", StringComparison.OrdinalIgnoreCase))
                    {
                        found.TrySetResult(true);
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the test ends or the timeout fires.
        }
    }
}
