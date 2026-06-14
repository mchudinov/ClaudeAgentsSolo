using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;

namespace DeveloperAgent.Tests.Resilience;

/// <summary>
/// Pins that <c>AddServiceDefaults()</c> does NOT stack a second, blanket standard resilience handler
/// on top of the per-client one the host deliberately configures.
/// </summary>
/// <remarks>
/// The Aspire template's <c>ConfigureHttpClientDefaults(http =&gt; http.AddStandardResilienceHandler())</c>
/// applies a resilience pipeline carrying the package's hard-coded <b>10s</b> per-attempt timeout to
/// EVERY <see cref="IHttpClientFactory"/> client. When a named client (e.g. <c>"anthropic"</c>) then adds
/// its own 60s-tuned handler (via <c>HttpResilienceConfigurator.Apply</c>), the two handlers stack — and
/// the outer blanket 10s <c>AttemptTimeout</c> fires first, cancelling slow Anthropic/GitHub calls
/// (observed live as a <c>TimeoutRejectedException</c> at <c>00:00:10</c> on the <c>-standard</c> pipeline,
/// with a <c>SocketException 995</c> aborting the in-flight read) and defeating the configured 60s.
/// Exactly ONE resilience handler must govern the named client.
/// </remarks>
public sealed class ServiceDefaultsResilienceWiringTests
{
    [Fact]
    public void Named_anthropic_client_has_exactly_one_resilience_handler()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceDefaults();

        // Mirror Program.cs: the host composes its own resilience handler onto the named client.
        builder.Services.AddAgentRuntimeServices(http => http.AddStandardResilienceHandler());

        using var provider = builder.Services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpMessageHandlerFactory>();
        using var handler = factory.CreateHandler(AnthropicHttpClients.ChatClient);

        CountResilienceHandlers(handler).Should().Be(1,
            because: "ServiceDefaults must not stack a blanket 10s resilience handler on top of the " +
                     "per-client 60s one — the blanket timeout fires first and defeats the tuning");
    }

    /// <summary>
    /// Walks the delegating-handler chain and counts the standard resilience handlers
    /// (<c>Microsoft.Extensions.Http.Resilience.ResilienceHandler</c>). The service-discovery
    /// <c>ResolvingHttpDelegatingHandler</c> in the same chain is deliberately not counted.
    /// </summary>
    private static int CountResilienceHandlers(HttpMessageHandler handler)
    {
        var count = 0;
        for (HttpMessageHandler? current = handler; current is DelegatingHandler dh; current = dh.InnerHandler)
        {
            if (current.GetType().FullName?.Contains("Resilience", StringComparison.Ordinal) == true)
                count++;
        }
        return count;
    }
}
