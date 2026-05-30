using DeveloperAgent.Actors;
using DeveloperAgent.Agent;
using DeveloperAgent.Agent.Mcp;
using DeveloperAgent.Agent.Tools;
using DeveloperAgent.AgentMemory;
using DeveloperAgent.Configuration;
using DeveloperAgent.Dashboard;
using DeveloperAgent.GitHub;
using DeveloperAgent.Lifecycle;
using DeveloperAgent.Resilience;
using DeveloperAgent.Sandbox;
using DeveloperAgent.Observability;
using Dapr.Client;
using Dapr.Workflow;
using DeveloperAgent.Workflow;
using DeveloperAgent.Workflow.Activities;
using DeveloperAgent.Workspace;
using Library;
using Microsoft.Extensions.Http.Resilience;
using OpenTelemetry.Metrics;
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
            // Step-27 (P2-L): a single RecentLogBuffer instance serves two roles — a
            // Serilog sink the main logger writes into AND the IRecentLogBuffer the
            // operator dashboard reads. The same instance is registered in DI below;
            // separate instances would leave the dashboard reading an empty buffer.
            var recentLogBuffer = new RecentLogBuffer(capacity: 200);
            var logger = new LoggerConfiguration()
                .ReadFrom.Configuration(builder.Configuration)
                .Enrich.FromLogContext()
                .WriteTo.Sink(recentLogBuffer)
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
                .Validate(o => o.FirstRetryIntervalSeconds > 0, "Agent.FirstRetryIntervalSeconds must be > 0")
                .ValidateOnStart();

            // ── Scope limits (Step-21, P2-H) ─────────────────────────────────────
            // Config-driven task scope-limit policy. Each cap must be positive; the
            // policy layer treats every limit as a hard halt-and-surface gate.
            builder.Services
                .AddOptions<ScopeLimitOptions>()
                .Bind(builder.Configuration.GetSection("ScopeLimits"))
                .Validate(o => o.MaxChangedFiles > 0, "ScopeLimits.MaxChangedFiles must be > 0")
                .Validate(o => o.MaxChangedLines > 0, "ScopeLimits.MaxChangedLines must be > 0")
                .Validate(o => o.MaxExecutionTimeSeconds > 0, "ScopeLimits.MaxExecutionTimeSeconds must be > 0")
                .Validate(o => o.MaxModelTurns > 0, "ScopeLimits.MaxModelTurns must be > 0")
                .Validate(o => o.MaxToolCalls > 0, "ScopeLimits.MaxToolCalls must be > 0")
                .Validate(o => o.MaxRetryCount > 0, "ScopeLimits.MaxRetryCount must be > 0")
                .Validate(o => o.MaxPRChangedFiles > 0, "ScopeLimits.MaxPRChangedFiles must be > 0")
                .Validate(o => o.MaxPRChangedLines > 0, "ScopeLimits.MaxPRChangedLines must be > 0")
                .ValidateOnStart();

            builder.Services
                .AddOptions<AnthropicOptions>()
                .Bind(builder.Configuration.GetSection("Anthropic"));

            builder.Services
                .AddOptions<GitHubOptions>()
                .Bind(builder.Configuration.GetSection("GitHub"));

            // Reviewer agent options (Step-28, P2-M).
            builder.Services
                .AddOptions<ReviewerOptions>()
                .Bind(builder.Configuration.GetSection("Reviewer"))
                .Validate(o => o.MaxDiffFiles > 0, "Reviewer.MaxDiffFiles must be > 0")
                .Validate(o => o.MaxDiffLines > 0, "Reviewer.MaxDiffLines must be > 0")
                .ValidateOnStart();

            builder.Services
                .AddOptions<WorkspaceOptions>()
                .Bind(builder.Configuration.GetSection("Workspace"))
                .Validate(o => !string.IsNullOrEmpty(o.RootPath), "Workspace.RootPath must not be empty")
                .Validate(o => o.AllowedCommands.Count > 0, "Workspace.AllowedCommands must not be empty")
                .ValidateOnStart();

            builder.Services
                .AddOptions<SandboxOptions>()
                .Bind(builder.Configuration.GetSection("Sandbox"));

            // ── Container isolation (Step-24, P2-I part 3/3) ──────────────────────
            // Per-shell_run isolation config. Disabled by default so the agent boots
            // without a container runtime; CommandSandbox routes shell_run through
            // IContainerRuntime only when Enabled is set.
            builder.Services
                .AddOptions<ContainerRuntimeOptions>()
                .Bind(builder.Configuration.GetSection("ContainerRuntime"));

            // ── MCP servers (Step-17, P2-F) ───────────────────────────────────────
            // Both servers are Enabled=false by default — the agent boots cleanly without
            // npx/node available. McpToolSource skips disabled servers silently and a
            // per-server connect failure logs a warning rather than aborting startup.
            builder.Services
                .AddOptions<McpOptions>()
                .Bind(builder.Configuration.GetSection("McpServers"));
            builder.Services.AddSingleton<IMcpClientConnector, StdioMcpClientConnector>();
            builder.Services.AddSingleton<IMcpToolSource, McpToolSource>();

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

            // ── Resilient HTTP clients (Step-26, P2-K) + egress filter (Step-23, P2-I) ──
            // Every external HTTP dependency the agent uses is reached through an
            // IHttpClientFactory-managed named HttpClient with two handlers composed
            // in this order (outer-to-inner):
            //   1. HostAllowlistHandler — denied hosts throw SandboxViolationException
            //      BEFORE any retry attempt (egress is the outermost layer so retries
            //      never re-hit a denied host).
            //   2. Standard resilience pipeline — timeouts + retries with exponential
            //      back-off + circuit breaker.
            // The Dapr Resiliency CRD (src/DeveloperAgent/dapr-components/resiliency.yaml)
            // covers cross-process service-invocation and state-store calls; the policies
            // below cover the in-process transport layer to Anthropic and GitHub.
            // HostAllowlistHandler itself is DI-registered further down as a transient.
            builder.Services
                .AddHttpClient(HttpClientNames.Anthropic)
                .AddHttpMessageHandler<HostAllowlistHandler>()
                .AddStandardResilienceHandler();

            builder.Services
                .AddHttpClient(HttpClientNames.GitHubRest)
                .AddHttpMessageHandler<HostAllowlistHandler>()
                .AddStandardResilienceHandler();

            builder.Services
                .AddHttpClient(HttpClientNames.GitHubGraphQL)
                .AddHttpMessageHandler<HostAllowlistHandler>()
                .AddStandardResilienceHandler();

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
            builder.Services.AddSingleton<IPathDenyPolicy, PathDenyPolicy>();
            builder.Services.AddSingleton<ICommandDenyPolicy, CommandDenyPolicy>();
            // DockerContainerRuntime has an internal ctor (depends on internal IProcessRunner),
            // so register it via factory lambda — same reason as CommandSandbox below.
            builder.Services.AddSingleton<IContainerRuntime>(sp => new DockerContainerRuntime(
                sp.GetRequiredService<IProcessRunner>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ContainerRuntimeOptions>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DockerContainerRuntime>>()));
            builder.Services.AddSingleton<ICommandSandbox>(sp => new CommandSandbox(
                sp.GetRequiredService<IProcessRunner>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WorkspaceOptions>>(),
                sp.GetRequiredService<IPathDenyPolicy>(),
                sp.GetRequiredService<ICommandDenyPolicy>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CommandSandbox>>(),
                sp.GetRequiredService<IContainerRuntime>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ContainerRuntimeOptions>>()));
            // HostAllowlistHandler is registered as a transient so the three named
            // HttpClients above (Anthropic, GitHubRest, GitHubGraphQL) can compose it
            // as their outer message handler; .AddHttpMessageHandler<T>() resolves a
            // new instance per request from the per-request scope.
            builder.Services.AddTransient<HostAllowlistHandler>();
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

            // ── Reviewer agent (Step-28, P2-M) ────────────────────────────────────
            // ReviewerPersonaLoader throws at construction if personas/reviewer.md is
            // missing or empty. ReviewerAgent reuses IAgentChatClientFactory so the same
            // resilient Anthropic transport applies; deterministic body/diff-size checks
            // run before any model call.
            builder.Services.AddSingleton<DeveloperAgent.Agent.ReviewerPersonaLoader>();
            builder.Services.AddSingleton<DeveloperAgent.Agent.Review.IReviewerAgent, DeveloperAgent.Agent.Review.ReviewerAgent>();

            // ── Observability ─────────────────────────────────────────────────────
            // AgentMetrics owns the "ClaudeAgentsSolo.DeveloperAgent" Meter; subscribe
            // it to the OTel metrics pipeline so its instruments flow to whatever
            // exporter ServiceDefaults wires up (OTEL_EXPORTER_OTLP_ENDPOINT etc).
            builder.Services.AddSingleton<AgentMetrics>();
            builder.Services.AddOpenTelemetry()
                .WithMetrics(m => m.AddMeter(AgentMetrics.MeterName));

            // ── Lifecycle ─────────────────────────────────────────────────────────
            builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
            // Step-11 (P2-B part 2/2): durable per-item state via ProgrammingTaskActor.
            // AddActors below registers IActorProxyFactory in DI; we wrap it with the
            // agent identity (machine name) and use that as the actor.AgentId field for
            // the TryClaimAsync invariant. InMemoryTaskStateStore stays in the codebase
            // for unit tests but is no longer the production registration.
            builder.Services.AddSingleton<ITaskStateStore>(sp => new DaprActorTaskStateStore(
                sp.GetRequiredService<Dapr.Actors.Client.IActorProxyFactory>(),
                Environment.MachineName));
            builder.Services.AddSingleton<ITaskExecutor, TaskExecutor>();
            builder.Services.AddHostedService<AgentLifecycleService>();

            // ── Step-18: AgentSession persistence (P2-G part 1/3) ─────────────────
            // DaprClient is constructed once via DaprClientBuilder (Dapr.Client 1.17.9).
            // DaprAgentSessionStore wraps it via the IDaprStateClient seam so it stays
            // unit-testable. AgentId = Environment.MachineName matches the
            // DaprActorTaskStateStore registration above.
            builder.Services.AddSingleton<DaprClient>(_ => new DaprClientBuilder().Build());
            builder.Services.AddSingleton<IDaprStateClient, DaprClientStateAdapter>();
            builder.Services.AddSingleton<IAgentSessionStore>(sp => new DaprAgentSessionStore(
                sp.GetRequiredService<IDaprStateClient>(),
                DaprAgentSessionStore.StateStoreName,
                Environment.MachineName));

            // ── Step-25: task-memory store (P2-J) ─────────────────────────────────
            // CompactMemoryActivity persists the post-Done summary under
            // task-memory:{projectItemId} via this store; DaprAgentMemoryContextProvider
            // (Step-20) reads the same namespace on a future run.
            builder.Services.AddSingleton<IAgentMemoryStore>(sp => new DaprAgentMemoryStore(
                sp.GetRequiredService<IDaprStateClient>(),
                DaprAgentMemoryStore.StateStoreName));

            // ── Dapr Actors ───────────────────────────────────────────────────────
            // Step-10 (P2-B part 1/2): register the ProgrammingTaskActor so the
            // runtime knows about it and the Dapr sidecar can invoke instances.
            // Step-11 will wire ITaskStateStore to call this actor; for now the
            // registration alone is enough for the actor handlers to be served.
            builder.Services.AddActors(opt => opt.Actors.RegisterActor<ProgrammingTaskActor>());

            // ── Dapr Workflow (Step-13, P2-D part 1/3) ───────────────────────────
            // DeveloperTaskWorkflow drives the full lifecycle of a single GitHub
            // project item. Workflow instance ID convention:
            //   "github-project-item-{itemId}"
            // The dispatcher (Step-14, P2-D part 2/3) schedules new instances.
            // AddDeveloperTaskWorkflow also bridges IDaprWorkflowClient → the concrete
            // DaprWorkflowClient (which AddDaprWorkflow registers) so AgentLifecycleService
            // and WaitForReviewActivity can resolve the interface they inject.
            builder.Services.AddDeveloperTaskWorkflow();

            // ── UI ────────────────────────────────────────────────────────────────
            // Step-27 (P2-L): operator dashboard. The RecentLogBuffer created above is
            // registered here as the shared IRecentLogBuffer read by the dashboard, and
            // IOperatorCommandService backs the pause/resume/cancel buttons.
            builder.Services.AddSingleton<IRecentLogBuffer>(recentLogBuffer);
            builder.Services.AddSingleton<IOperatorCommandService, OperatorCommandService>();
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

            // Step-10: expose the Dapr Actors HTTP endpoints
            // (dapr/config, actors/{type}/{id}/method/..., reminders, timers).
            app.MapActorsHandlers();

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
