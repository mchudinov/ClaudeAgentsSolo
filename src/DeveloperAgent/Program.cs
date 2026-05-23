using DeveloperAgent.Agent;
using DeveloperAgent.Agent.Tools;
using DeveloperAgent.Configuration;
using DeveloperAgent.GitHub;
using DeveloperAgent.Lifecycle;
using DeveloperAgent.Workspace;
using Library;
using Serilog;
using Serilog.Debugging;
using Serilog.Events;
using MudBlazor.Services;

namespace DeveloperAgent;

public class Program
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
            Serilog.Log.Logger.Information("DeveloperAgent is starting");
            Serilog.Log.Logger.Debug(".NET Version: {DotNetVersion}", Environment.Version);
            Serilog.Log.Logger.Debug("► Environment variables");
            Environment.GetEnvironmentVariables().OutputEnvironmentVariables();

            var builder = WebApplication.CreateBuilder(args);

            // ── Configuration sources ────────────────────────────────────────────
            builder.Configuration
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables();

            // ── Logging ──────────────────────────────────────────────────────────
            var logger = new LoggerConfiguration()
                .ReadFrom.Configuration(builder.Configuration)
                .Enrich.FromLogContext()
                .CreateLogger();
            builder.Logging.ClearProviders();
            builder.Logging.AddSerilog(logger);

            // ── Telemetry ─────────────────────────────────────────────────────────
            builder.AddOpenTelemetry();

            // ── Options — bind + eager validate intrinsic constraints ────────────
            // GitHub identity (Owner, Url, Project.Number) is validated by the hosted
            // service (plan 05), not at startup, so dotnet run works on a fresh checkout.
            builder.Services
                .AddOptions<AgentOptions>()
                .Bind(builder.Configuration.GetSection("Agent"))
                .Validate(o => o.PollIntervalSeconds > 0, "Agent.PollIntervalSeconds must be > 0")
                .Validate(o => o.ReviewPollIntervalSeconds > 0, "Agent.ReviewPollIntervalSeconds must be > 0")
                .Validate(o => o.MaxModelTurnsHardCap > 0, "Agent.MaxModelTurnsHardCap must be > 0")
                .ValidateOnStart();

            builder.Services
                .AddOptions<AnthropicOptions>()
                .Bind(builder.Configuration.GetSection("Anthropic"));

            builder.Services
                .AddOptions<GitHubOptions>()
                .Bind(builder.Configuration.GetSection("GitHub"));

            builder.Services
                .AddOptions<WorkspaceOptions>()
                .Bind(builder.Configuration.GetSection("Workspace"))
                .Validate(o => !string.IsNullOrEmpty(o.RootPath), "Workspace.RootPath must not be empty")
                .Validate(o => o.AllowedCommands.Count > 0, "Workspace.AllowedCommands must not be empty")
                .ValidateOnStart();

            // ── Secret resolution — eager at startup ──────────────────────────────
            builder.Services.AddSingleton<ISecretResolver, EnvAndUserSecretsResolver>();
            builder.Services.AddSingleton<SecretsBundle>(sp =>
            {
                var resolver = sp.GetRequiredService<ISecretResolver>();
                var anthropicOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AnthropicOptions>>().Value;
                var githubOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GitHubOptions>>().Value;
                return new SecretsBundle(
                    AnthropicApiKey: resolver.Resolve(anthropicOptions.ApiKeySecretName),
                    GitHubToken: resolver.Resolve(githubOptions.TokenSecretName));
            });

            // ── GitHub service ────────────────────────────────────────────────────
            // Singletons are lazy: construction tolerates empty GitHubOptions.
            // First use will fail fast if required config (Owner, etc.) is absent.
            builder.Services.AddSingleton<IGraphQLTransport, OctokitGraphQLTransport>();
            builder.Services.AddSingleton<IRestTransport, OctokitRestTransport>();
            builder.Services.AddSingleton<IGitHubProjectService, GitHubProjectService>();

            // ── Workspace / Git / Sandbox ─────────────────────────────────────
            // IProcessRunner and CommandSandbox have internal constructors (IProcessRunner
            // is internal), so DI reflection cannot see them. Register via factory lambdas
            // which execute inside this assembly and can access internal members.
            builder.Services.AddSingleton<IProcessRunner>(_ => new DefaultProcessRunner());
            builder.Services.AddSingleton<ICommandSandbox>(sp => new CommandSandbox(
                sp.GetRequiredService<IProcessRunner>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WorkspaceOptions>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CommandSandbox>>()));
            builder.Services.AddSingleton<IGitClient, GitClient>();
            builder.Services.AddSingleton<IWorkspaceManager, WorkspaceManager>();

            // ── Agent ─────────────────────────────────────────────────────────────
            // PersonaLoader throws at construction if persona file is missing or empty.
            // AnthropicChatClientFactory resolves the API key lazily (on first Create) so
            // an unconfigured dotnet run does not crash.
            builder.Services.AddSingleton<PersonaLoader>();
            builder.Services.AddSingleton<IAgentChatClientFactory, AnthropicChatClientFactory>();
            builder.Services.AddSingleton<ITool, ReadFileTool>();
            builder.Services.AddSingleton<ITool, WriteFileTool>();
            builder.Services.AddSingleton<ITool, EditFileTool>();
            builder.Services.AddSingleton<ITool, ListDirectoryTool>();
            builder.Services.AddSingleton<ITool, ShellRunTool>();
            builder.Services.AddSingleton<ITool, CommentOnItemTool>();
            builder.Services.AddSingleton<ITool, CreatePullRequestTool>();
            builder.Services.AddSingleton<IAgentRunner, AnthropicAgentRunner>();

            // ── Lifecycle ─────────────────────────────────────────────────────────
            builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
            builder.Services.AddSingleton<ITaskStateStore, InMemoryTaskStateStore>();
            builder.Services.AddSingleton<TaskExecutor>();
            builder.Services.AddHostedService<AgentLifecycleService>();

            // ── UI ────────────────────────────────────────────────────────────────
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();
            builder.Services.AddMudServices();

            // ── Build ─────────────────────────────────────────────────────────────
            var app = builder.Build();

            if (!app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }

            app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
            app.UseRouting();
            app.UseAntiforgery();
            app.UseAuthorization();
            app.MapStaticAssets();
            app.MapDefaultEndpoints(applicationStartTime);

            app.MapRazorComponents<DeveloperAgent.Components.App>()
                .AddInteractiveServerRenderMode();

            app.MapGet("/info", (ITaskStateStore taskStateStore) =>
            {
                var task = taskStateStore.Current;
                object? taskDto = task is null
                    ? null
                    : new
                    {
                        projectItemId = task.ProjectItemId,
                        issueNumber = task.IssueNumber,
                        title = task.Title,
                        phase = task.Phase.ToString(),
                        branchName = task.BranchName,
                        pullRequestNumber = task.PullRequestNumber,
                        lastError = task.LastError,
                        startedAtUtc = task.StartedAtUtc,
                        updatedAtUtc = task.UpdatedAtUtc
                    };

                return Results.Json(new
                {
                    endpoints = new[] { "/livez", "/uptime", "/info", "/health", "/alive" },
                    task = taskDto
                });
            });

            Serilog.Log.Logger.Information("► Final configuration");
            (builder.Configuration as Microsoft.Extensions.Configuration.IConfigurationRoot)
                ?.AllConfigurationKeys().LogStrings();

            app.Run();
        }
        catch (Exception ex)
        {
            Serilog.Log.Fatal(ex, "DeveloperAgent process terminated unexpectedly.");
        }
        finally
        {
            Serilog.Log.Information("Shut down complete.");
            Serilog.Log.CloseAndFlush();
        }
    }
}
