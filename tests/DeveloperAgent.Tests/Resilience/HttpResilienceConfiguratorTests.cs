using System.Net;
using DeveloperAgent.Resilience;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace DeveloperAgent.Tests.Resilience;

/// <summary>
/// <see cref="HttpResilienceOptions"/> default + binding contract, and the
/// <see cref="HttpResilienceConfigurator"/> rules that translate the configured
/// per-attempt timeout into a <see cref="HttpStandardResilienceOptions"/> that
/// still passes the package's startup validator.
/// </summary>
public sealed class HttpResilienceOptionsTests
{
    [Fact]
    public void Default_attempt_timeout_is_30_seconds()
    {
        // The standard resilience handler ships a 10s per-attempt timeout, which was
        // cancelling slow Anthropic streaming calls. The agent default is raised to 30s.
        new HttpResilienceOptions().AttemptTimeoutSeconds.Should().Be(30);
    }

    [Fact]
    public void Binding_from_configuration_overrides_default()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HttpResilience:AttemptTimeoutSeconds"] = "45",
            })
            .Build();

        var options = new HttpResilienceOptions();
        config.GetSection("HttpResilience").Bind(options);

        options.AttemptTimeoutSeconds.Should().Be(45);
    }
}

public sealed class HttpResilienceConfiguratorTests
{
    [Theory]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(120)]
    public void Apply_sets_attempt_timeout_from_configured_seconds(int seconds)
    {
        var resilience = new HttpStandardResilienceOptions();

        HttpResilienceConfigurator.Apply(resilience, new HttpResilienceOptions { AttemptTimeoutSeconds = seconds });

        resilience.AttemptTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(seconds));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(120)]
    public void Apply_keeps_total_request_timeout_strictly_above_attempt_timeout(int seconds)
    {
        // HttpStandardResilienceOptions validator rule: TotalRequestTimeout must be a
        // higher timeout than the attempt timeout — equal or lower fails ValidateOnStart.
        var resilience = new HttpStandardResilienceOptions();

        HttpResilienceConfigurator.Apply(resilience, new HttpResilienceOptions { AttemptTimeoutSeconds = seconds });

        resilience.TotalRequestTimeout.Timeout.Should().BeGreaterThan(resilience.AttemptTimeout.Timeout);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(120)]
    public void Apply_keeps_sampling_duration_at_least_double_attempt_timeout(int seconds)
    {
        // HttpStandardResilienceOptions validator rule: the circuit-breaker sampling
        // duration must be at least 2× the attempt timeout.
        var resilience = new HttpStandardResilienceOptions();

        HttpResilienceConfigurator.Apply(resilience, new HttpResilienceOptions { AttemptTimeoutSeconds = seconds });

        resilience.CircuitBreaker.SamplingDuration.Should()
            .BeGreaterThanOrEqualTo(TimeSpan.FromTicks(resilience.AttemptTimeout.Timeout.Ticks * 2));
    }

    [Fact]
    public async Task Configured_pipeline_builds_and_sends_without_a_validation_error()
    {
        // End-to-end: wiring Apply into the real AddStandardResilienceHandler must produce
        // options the package's validator accepts. Building + sending through the pipeline
        // triggers that validation; an invalid timeout combo would throw here.
        var services = new ServiceCollection();
        services.AddHttpClient("probe")
            .ConfigurePrimaryHttpMessageHandler(() => new OkHandler())
            .AddStandardResilienceHandler(o => HttpResilienceConfigurator.Apply(o, new HttpResilienceOptions()));

        var http = services.BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("probe");

        var response = await http.GetAsync("https://example.invalid/probe");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request });
    }
}
