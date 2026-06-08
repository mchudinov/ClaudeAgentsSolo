using Agent.Sandbox;
using Library;
using Library.Secrets;
using Microsoft.Extensions.Http.Resilience;
using ReviewerAgent.Configuration;
using ReviewerAgent.Lifecycle;
using ServiceDefaults.Resilience;
using Serilog;
using Serilog.Debugging;
using Serilog.Events;

namespace ReviewerAgent;

public partial class Program
{
    public static void Main(string[] args)
    {
        Serilog.Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Override("Default", LogEventLevel.Debug)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        SelfLog.Enable(Console.Error);

        try
        {
            var applicationStartTime = DateTimeOffset.UtcNow;
            Serilog.Log.Logger.Information("ReviewerAgent is starting");

            var builder = WebApplication.CreateBuilder(args);

            builder.Configuration
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables();

            var logger = new LoggerConfiguration()
                .ReadFrom.Configuration(builder.Configuration)
                .Enrich.FromLogContext()
                .CreateLogger();
            builder.Logging.ClearProviders();
            builder.Logging.AddSerilog(logger);

            builder.AddServiceDefaults();
            builder.AddOpenTelemetry();

            // ── Options ───────────────────────────────────────────────────────────
            builder.Services
                .AddOptions<ReviewerOptions>()
                .Bind(builder.Configuration.GetSection("Reviewer"))
                .Validate(o => o.MaxDiffFiles > 0, "Reviewer.MaxDiffFiles must be > 0")
                .Validate(o => o.MaxDiffLines > 0, "Reviewer.MaxDiffLines must be > 0")
                .Validate(o => !string.IsNullOrWhiteSpace(o.Model), "Reviewer.Model must not be empty")
                .ValidateOnStart();

            builder.Services
                .AddOptions<ReviewPollingOptions>()
                .Bind(builder.Configuration.GetSection("Reviewer"))
                .Validate(o => o.PollIntervalSeconds > 0, "Reviewer.PollIntervalSeconds must be > 0")
                .ValidateOnStart();

            builder.Services
                .AddOptions<AnthropicOptions>()
                .Bind(builder.Configuration.GetSection("Anthropic"));

            builder.Services
                .AddOptions<GitHubOptions>()
                .Bind(builder.Configuration.GetSection("GitHub"));

            builder.Services
                .AddOptions<HttpResilienceOptions>()
                .Bind(builder.Configuration.GetSection("HttpResilience"))
                .Validate(o => o.AttemptTimeoutSeconds > 0, "HttpResilience.AttemptTimeoutSeconds must be > 0")
                .ValidateOnStart();

            // Only AllowedHosts is consumed (by HostAllowlistHandler); the deny lists default to [].
            builder.Services
                .AddOptions<SandboxOptions>()
                .Bind(builder.Configuration.GetSection("Sandbox"))
                .Validate(o => o.AllowedHosts.Count > 0, "Sandbox.AllowedHosts must not be empty")
                .ValidateOnStart();

            // ── Secrets ─────────────────────────────────────────────────────────────
            builder.Services.AddSingleton<ISecretResolver, EnvAndUserSecretsResolver>();
            builder.Services.AddSingleton<SecretsBundle>(sp =>
            {
                var resolver = sp.GetRequiredService<ISecretResolver>();
                var anthropic = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AnthropicOptions>>().Value;
                var github = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GitHubOptions>>().Value;
                return new SecretsBundle(
                    AnthropicApiKey: resolver.Resolve(anthropic.ApiKeySecretName),
                    GitHubToken: resolver.Resolve(github.TokenSecretName));
            });

            // ── Egress filter + resilient HTTP clients ───────────────────────────────
            var httpResilience = new HttpResilienceOptions();
            builder.Configuration.GetSection("HttpResilience").Bind(httpResilience);
            builder.Services.AddTransient<HostAllowlistHandler>();

            builder.Services.AddSingleton<IAnthropicApiKeyProvider, SecretsBundleAnthropicApiKeyProvider>();
            builder.Services.AddAgentRuntimeServices(http => http
                .AddHttpMessageHandler<HostAllowlistHandler>()
                .AddStandardResilienceHandler(o => HttpResilienceConfigurator.Apply(o, httpResilience)));

            builder.Services.AddSingleton<IGitHubTokenProvider, SecretsBundleGitHubTokenProvider>();
            builder.Services.AddGitHubProjectServices(http => http
                .AddHttpMessageHandler<HostAllowlistHandler>()
                .AddStandardResilienceHandler(o => HttpResilienceConfigurator.Apply(o, httpResilience)));

            // ── Reviewer engine + polling loop ───────────────────────────────────────
            builder.Services.AddReviewServices();
            builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
            builder.Services.AddHostedService<ReviewLifecycleService>();

            var app = builder.Build();

            app.MapDefaultEndpoints(applicationStartTime);

            app.MapGet("/info", () => Results.Json(new
            {
                service = "ReviewerAgent",
                endpoints = new[] { "/livez", "/uptime", "/info", "/review/{prNumber}", "/health", "/alive" }
            }));

            app.Run();
        }
        catch (Exception ex)
        {
            Serilog.Log.Fatal(ex, "ReviewerAgent process terminated unexpectedly.");
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
        }
    }
}

// Expose the implicit Program type for Aspire.Hosting.Testing / WebApplicationFactory.
public partial class Program;
