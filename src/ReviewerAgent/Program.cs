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

            ConfigureConfigSources(builder.Configuration, builder.Environment);

            var logger = new LoggerConfiguration()
                .ReadFrom.Configuration(builder.Configuration)
                .Enrich.FromLogContext()
                .CreateLogger();
            builder.Logging.ClearProviders();
            builder.Logging.AddSerilog(logger);

            builder.AddServiceDefaults();
            builder.AddOpenTelemetry();

            // ── Options ───────────────────────────────────────────────────────────
            // Agent-identity settings live in the shared "Agent" section — the same structure the
            // DeveloperAgent host uses (Name/Model/Effort/PersonaPath/poll cadence). The engine's
            // ReviewerOptions and the polling loop's ReviewPollingOptions source Model/PersonaPath/
            // PollIntervalSeconds from "Agent"; review-specific knobs (diff caps, required PR-body
            // sections, idempotency login, draft/author filters) stay in "Reviewer".
            builder.Services
                .AddOptions<AgentOptions>()
                .Bind(builder.Configuration.GetSection("Agent"))
                .Validate(o => !string.IsNullOrWhiteSpace(o.Model), "Agent.Model must not be empty")
                .Validate(o => o.PollIntervalSeconds > 0, "Agent.PollIntervalSeconds must be > 0")
                .ValidateOnStart();

            // ReviewerOptions: Model + PersonaPath from "Agent"; diff-size caps from "ScopeLimits"
            // (the reviewer analog of the DeveloperAgent host's ScopeLimits section); required PR-body
            // sections from "Reviewer". The three sections are disjoint, so binding them in turn never
            // touches the same key twice and RequiredPrBodySections loads exactly once (Step-41).
            builder.Services
                .AddOptions<ReviewerOptions>()
                .Bind(builder.Configuration.GetSection("Agent"))
                .Bind(builder.Configuration.GetSection("Reviewer"))
                .Bind(builder.Configuration.GetSection("ScopeLimits"))
                .Validate(o => o.MaxDiffFiles > 0, "ScopeLimits.MaxDiffFiles must be > 0")
                .Validate(o => o.MaxDiffLines > 0, "ScopeLimits.MaxDiffLines must be > 0")
                .Validate(o => !string.IsNullOrWhiteSpace(o.Model), "Agent.Model must not be empty")
                .ValidateOnStart();

            // ReviewPollingOptions: PollIntervalSeconds from "Agent"; login/draft/author filters
            // from "Reviewer" (AuthorAllowList lives only in "Reviewer" → single load).
            builder.Services
                .AddOptions<ReviewPollingOptions>()
                .Bind(builder.Configuration.GetSection("Agent"))
                .Bind(builder.Configuration.GetSection("Reviewer"))
                .Validate(o => o.PollIntervalSeconds > 0, "Agent.PollIntervalSeconds must be > 0")
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

            // Bound "Agent" settings are available now — log the configured identity/model.
            var agentOptions = app.Services
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value;
            Serilog.Log.Logger.Information(
                "{AgentName} configured: model={Model} effort={Effort} pollIntervalSeconds={PollIntervalSeconds}",
                agentOptions.Name, agentOptions.Model, agentOptions.Effort, agentOptions.PollIntervalSeconds);

            app.MapDefaultEndpoints(applicationStartTime);

            app.MapGet("/info", () => Results.Json(new
            {
                service = "ReviewerAgent",
                endpoints = new[] { "/livez", "/uptime", "/info", "/review/{prNumber}", "/health", "/alive" }
            }));

            app.MapPost("/review/{prNumber:int}", async (
                int prNumber, IReviewerAgent reviewer, CancellationToken ct) =>
            {
                var result = await reviewer.ReviewAsync(prNumber, ct);
                return Results.Json(new
                {
                    prNumber,
                    verdict = result.Verdict.ToString(),
                    usedModel = result.UsedModel,
                    summary = result.Summary
                });
            });

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

    /// <summary>
    /// Registers the host's configuration sources. Exposed so tests can assert the source wiring
    /// without starting the host (the polling loop reviews live PRs on startup).
    /// </summary>
    public static void ConfigureConfigSources(IConfigurationBuilder configuration, IHostEnvironment environment)
    {
        configuration
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile($"appsettings.{environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
            // WebApplication.CreateBuilder registers the User Secrets provider only in Development.
            // Add it explicitly so the shared User Secrets store (anthropic-api-key / github-token)
            // resolves in every environment — when launched without ASPNETCORE_ENVIRONMENT=Development
            // or under the Aspire AppHost. No-ops in containers where the store dir is absent, leaving
            // the resolver's environment-variable tier to take over.
            .AddUserSecrets(typeof(Program).Assembly, optional: true, reloadOnChange: true)
            .AddEnvironmentVariables();
    }
}

// Expose the implicit Program type for Aspire.Hosting.Testing / WebApplicationFactory.
public partial class Program;
