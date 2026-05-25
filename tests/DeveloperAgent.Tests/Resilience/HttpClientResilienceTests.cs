using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace DeveloperAgent.Tests.Resilience;

/// <summary>
/// Verifies that <c>AddStandardResilienceHandler()</c> attached to the named
/// HttpClient (per Step-26, P2-K) actually retries on transient failures and
/// gives up cleanly after the configured cap. The Program.cs registration
/// wires the standard handler to three named clients — these tests use the
/// same name + the same package's defaults to assert the contract, without
/// going through Program.cs (and without requiring real Anthropic / GitHub
/// network access).
/// </summary>
public sealed class HttpClientResilienceTests
{
    private const string ClientName = "test-client";

    [Fact]
    public async Task Named_client_with_StandardResilienceHandler_retries_transient_503_until_success()
    {
        var handler = new RecordingHandler(scenario: ScriptedResponses.ThreeServiceUnavailableThenOk);

        var http = BuildClient(handler);
        var response = await http.GetAsync("https://example.invalid/probe");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.CallCount.Should().BeGreaterThanOrEqualTo(2,
            because: "the standard resilience pipeline must retry at least once after a 503 " +
                     "before succeeding on the OK response");
        handler.CallCount.Should().BeLessThanOrEqualTo(6,
            because: "the standard resilience pipeline caps retries — runaway retries " +
                     "would compound latency");
    }

    [Fact]
    public async Task Named_client_with_StandardResilienceHandler_gives_up_after_persistent_503()
    {
        var handler = new RecordingHandler(scenario: ScriptedResponses.AlwaysServiceUnavailable);

        var http = BuildClient(handler);
        // Use a generous client timeout so we measure the pipeline's give-up, not Kestrel's.
        http.Timeout = TimeSpan.FromMinutes(2);

        var response = await http.GetAsync("https://example.invalid/probe");

        // After the standard resilience pipeline exhausts retries, the LAST 503 is
        // propagated back to the caller (the pipeline does not transform the response).
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        handler.CallCount.Should().BeGreaterThanOrEqualTo(2,
            because: "the pipeline must attempt the original request + at least one retry");
        handler.CallCount.Should().BeLessThanOrEqualTo(10,
            because: "the standard resilience pipeline must not retry forever");
    }

    [Fact]
    public async Task Named_client_with_StandardResilienceHandler_does_not_retry_on_400_BadRequest()
    {
        // 4xx (except 408 / 429) is a client error and must NOT be retried — retrying
        // a bad payload is wasted work and amplifies load on the upstream.
        var handler = new RecordingHandler(scenario: ScriptedResponses.AlwaysBadRequest);

        var http = BuildClient(handler);
        var response = await http.GetAsync("https://example.invalid/probe");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        handler.CallCount.Should().Be(1,
            because: "the standard resilience pipeline must not retry on 400 BadRequest");
    }

    /// <summary>
    /// Build a fresh HttpClient instance with a fresh resilience pipeline wrapping
    /// the supplied stub handler. Each test builds its own ServiceProvider so the
    /// per-test handler does not leak resilience state (circuit-breaker counters)
    /// between tests.
    /// </summary>
    private static HttpClient BuildClient(HttpMessageHandler primaryHandler)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => primaryHandler)
            .AddStandardResilienceHandler(o =>
            {
                // Keep the test fast: shrink the retry delay so the test does not
                // block on the production back-off schedule. The DEFAULTS we ship
                // in production are unchanged — this only affects the stub.
                o.Retry.UseJitter = false;
                o.Retry.Delay = TimeSpan.FromMilliseconds(20);
                o.Retry.BackoffType = Polly.DelayBackoffType.Constant;
                // Don't let the per-attempt timeout fire on the recording handler.
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(2);
            });

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);
    }

    private enum ScriptedResponses
    {
        ThreeServiceUnavailableThenOk,
        AlwaysServiceUnavailable,
        AlwaysBadRequest,
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly ScriptedResponses _scenario;
        private int _calls;

        public RecordingHandler(ScriptedResponses scenario)
        {
            _scenario = scenario;
        }

        public int CallCount => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _calls);
            HttpResponseMessage response = _scenario switch
            {
                ScriptedResponses.ThreeServiceUnavailableThenOk =>
                    attempt <= 3
                        ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") },
                ScriptedResponses.AlwaysServiceUnavailable =>
                    new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                ScriptedResponses.AlwaysBadRequest =>
                    new HttpResponseMessage(HttpStatusCode.BadRequest),
                _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            };
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
