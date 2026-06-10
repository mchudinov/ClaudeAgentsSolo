using DeveloperAgent.Actors;
using DeveloperAgent.Agent;
using DeveloperAgent.Agent.Tools;
using DeveloperAgent.Configuration;
using DeveloperAgent.Dashboard;
using DeveloperAgent.GitHub;
using DeveloperAgent.Lifecycle;
using DeveloperAgent.Observability;
using DeveloperAgent.Triage;
using Dapr.Client;
using Dapr.Workflow;
using Library.Logging;
using Library.Secrets;
using DeveloperAgent.Workflow;
using DeveloperAgent.Workflow.Activities;
using Agent.Workspace;
using Library;
using ServiceDefaults.Resilience;
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
                .Validate(o => o.MaxExecutionTimeSeconds > 0, "ScopeLimits.MaxExecutionTimeSeconds must be > 0")
                .Validate(o => o.MaxModelTurns > 0, "ScopeLimits.MaxModelTurns must be > 0")
                .Validate(o => o.MaxToolCalls > 0, "ScopeLimits.MaxToolCalls must be > 0")
                .Validate(o => o.MaxRetryCount > 0, "ScopeLimits.MaxRetryCount must be > 0")
                .Validate(o => o.MaxPRChangedFiles > 0, "ScopeLimits.MaxPRChangedFiles must be > 0")
                .Validate(o => o.MaxPRChangedLines > 0, "ScopeLimits.MaxPRChangedLines must be > 0")
                .ValidateOnStart();

            // Step-50: the pre-push diff-scope caps were carved out of ScopeLimitOptions into
            // their own record so the git/workspace layer depends only on these two limits.
            // Bound from the SAME ScopeLimits section (both halves bind from one section; these
            // are scalars, so the ConfigurationBinder append-on-default gotcha does not apply).
            builder.Services
                .AddOptions<DiffScopeLimitOptions>()
                .Bind(builder.Configuration.GetSection("ScopeLimits"))
                .Validate(o => o.MaxChangedFiles > 0, "ScopeLimits.MaxChangedFiles must be > 0")
                .Validate(o => o.MaxChangedLines > 0, "ScopeLimits.MaxChangedLines must be > 0")
                .ValidateOnStart();

            builder.Services
                .AddOptions<AnthropicOptions>()
                .Bind(builder.Configuration.GetSection("Anthropic"));

            builder.Services
                .AddOptions<GitHubOptions>()
                .Bind(builder.Configuration.GetSection("GitHub"));

            // ProjectStateNames is bound standalone from GitHub:States so the developer-agent
            // lifecycle facade (GitHubProjectService) owns the ProjectState↔column-name mapping
            // independently of GitHubOptions — the agent-neutral client never sees these names.
            builder.Services
                .AddOptions<ProjectStateNames>()
                .Bind(builder.Configuration.GetSection("GitHub:States"));

            // ── Relevance triage (Step-54) ──────────────────────────────────────
            // The triage gate decides, before any work, whether a picked-up item is in-scope for the
            // project and within the agent's skill; out-of-scope items are parked in Backlog. Disabled
            // by default in the record so a host without a Triage section boots unchanged; the shipped
            // appsettings.json turns it on. Fail closed at startup: an enabled gate with no RepoScope
            // is a misconfiguration (it would have nothing to judge against).
            builder.Services
                .AddOptions<TriageOptions>()
                .Bind(builder.Configuration.GetSection("Triage"))
                .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.RepoScope),
                    "Triage.RepoScope must be set when Triage.Enabled is true")
                .ValidateOnStart();

            builder.Services
                .AddOptions<WorkspaceOptions>()
                .Bind(builder.Configuration.GetSection("Workspace"))
                .Validate(o => !string.IsNullOrEmpty(o.RootPath), "Workspace.RootPath must not be empty")
                .Validate(o => o.AllowedCommands.Count > 0, "Workspace.AllowedCommands must not be empty")
                .ValidateOnStart();

            // Step-50: the workspace-manager side of the workspace config (RootPath only) was
            // carved into its own record so the git/workspace layer does not see the sandbox-only
            // AllowedCommands allowlist. Bound from the SAME Workspace section (RootPath is a scalar
            // shared with the sandbox's WorkspaceOptions.RootPath — no double-append).
            builder.Services
                .AddOptions<WorkspaceRootOptions>()
                .Bind(builder.Configuration.GetSection("Workspace"))
                .Validate(o => !string.IsNullOrEmpty(o.RootPath), "Workspace.RootPath must not be empty")
                .ValidateOnStart();

            // The deny/allow lists live solely in appsettings.json (Step-41 — the records
            // carry no in-code defaults), so require the security-critical lists to be
            // present: an empty list would silently run the sandbox unguarded.
            builder.Services
                .AddOptions<SandboxOptions>()
                .Bind(builder.Configuration.GetSection("Sandbox"))
                .Validate(o => o.DeniedCommands.Count > 0, "Sandbox.DeniedCommands must not be empty")
                .Validate(o => o.DenyPathPatterns.Count > 0, "Sandbox.DenyPathPatterns must not be empty")
                .Validate(o => o.AllowedHosts.Count > 0, "Sandbox.AllowedHosts must not be empty")
                .ValidateOnStart();

            // ── HTTP resilience tunables (Step-32) ────────────────────────────────
            // Per-attempt timeout for the named HttpClients below. The standard
            // handler's 10s default cancels slow Anthropic streaming calls; default 30s.
            builder.Services
                .AddOptions<HttpResilienceOptions>()
                .Bind(builder.Configuration.GetSection("HttpResilience"))
                .Validate(o => o.AttemptTimeoutSeconds > 0, "HttpResilience.AttemptTimeoutSeconds must be > 0")
                .ValidateOnStart();

            // ── Container isolation (Step-24, P2-I part 3/3) ──────────────────────
            // Per-shell_run isolation config. Disabled by default so the agent boots
            // without a container runtime; CommandSandbox routes shell_run through
            // IContainerRuntime only when Enabled is set.
            builder.Services
                .AddOptions<ContainerRuntimeOptions>()
                .Bind(builder.Configuration.GetSection("ContainerRuntime"));

            // ── MCP servers (Step-17, P2-F; extracted to Agent.Mcp in Step-45) ────
            // Servers are Enabled=false by default — the agent boots cleanly without
            // npx/node available. McpToolSource skips disabled servers silently and a
            // per-server connect failure logs a warning rather than aborting startup.
            // AddMcpServices binds McpOptions from McpServers:Servers and registers the
            // stdio connector + tool source.
            builder.Services.AddMcpServices(builder.Configuration);

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
            // The Dapr Resiliency CRD (src/AppHost/dapr/components/resiliency.yaml)
            // covers cross-process service-invocation and state-store calls; the policies
            // below cover the in-process transport layer to Anthropic and GitHub.
            // HostAllowlistHandler itself is DI-registered further down as a transient.
            // The per-attempt timeout (and the dependent total/circuit-breaker windows)
            // come from the HttpResilience section via HttpResilienceConfigurator so a
            // slow Anthropic stream is not cancelled at the 10s standard-handler default.
            var httpResilience = new HttpResilienceOptions();
            builder.Configuration.GetSection("HttpResilience").Bind(httpResilience);

            // The Anthropic chat-client transport (IAgentChatClientFactory) and its named HttpClient
            // ("anthropic") are registered by the Agent.Runtime library's AddAgentRuntimeServices.
            // The host supplies: (a) the API key via IAnthropicApiKeyProvider (so only the key, never
            // the GitHub token, reaches the runtime layer); and (b) the egress filter
            // (HostAllowlistHandler) + standard resilience pipeline composed onto the named client
            // through the callback — mirroring the GitHub registration below.
            builder.Services.AddSingleton<IAnthropicApiKeyProvider, SecretsBundleAnthropicApiKeyProvider>();
            builder.Services.AddAgentRuntimeServices(http => http
                .AddHttpMessageHandler<HostAllowlistHandler>()
                .AddStandardResilienceHandler(o => HttpResilienceConfigurator.Apply(o, httpResilience)));

            // ── GitHub service ────────────────────────────────────────────────────
            // The agent-neutral GitHub Projects client, its Octokit transports, and the two named
            // HttpClients (github-rest / github-graphql) are registered by the Agent.GitHub
            // library's AddGitHubProjectServices. The host supplies: (a) the GitHub token via
            // IGitHubTokenProvider (so only the token, never the Anthropic key, reaches the GitHub
            // layer); (b) the egress filter (HostAllowlistHandler) + standard resilience pipeline
            // composed onto each named client through the callback; and (c) the developer-agent
            // lifecycle facade (GitHubProjectService) that maps ProjectState ↔ board column names.
            builder.Services.AddSingleton<IGitHubTokenProvider, SecretsBundleGitHubTokenProvider>();
            builder.Services.AddGitHubProjectServices(http => http
                .AddHttpMessageHandler<HostAllowlistHandler>()
                .AddStandardResilienceHandler(o => HttpResilienceConfigurator.Apply(o, httpResilience)));
            builder.Services.AddSingleton<IGitHubProjectService, GitHubProjectService>();

            // ── Workspace / Git / Sandbox ─────────────────────────────────────
            // The agent-neutral command/file/egress sandbox (IProcessRunner, IPathDenyPolicy,
            // ICommandDenyPolicy, IContainerRuntime, ICommandSandbox, and the transient
            // HostAllowlistHandler) is registered by the Agent.Sandbox library's AddSandboxServices
            // (Step-49). The internal-ctor factory wiring that used to live inline here now lives in
            // the library; the host keeps the appsettings Sandbox/Workspace/ContainerRuntime option
            // bindings + ValidateOnStart above, and composes HostAllowlistHandler onto the Anthropic
            // and GitHub named clients via the AddAgentRuntimeServices/AddGitHubProjectServices
            // callbacks above (the sandbox owns no named HttpClient).
            builder.Services.AddSandboxServices();

            // The agent-neutral git client + workspace manager (IGitClient/GitClient,
            // IWorkspaceManager/WorkspaceManager) are registered by the Agent.Workspace library's
            // AddWorkspaceGitServices (Step-51). The host supplies the GitHub token via
            // IGitTokenProvider (a SecretsBundle adapter, mirroring IGitHubTokenProvider) so the
            // workspace layer carries no Agent.GitHub/Octokit dependency; repoUrl flows as data —
            // read from GitHubOptions by the lifecycle callers and passed to Prepare/Push.
            builder.Services.AddSingleton<IGitTokenProvider, SecretsBundleGitTokenProvider>();
            builder.Services.AddWorkspaceGitServices();

            // ── Agent ─────────────────────────────────────────────────────────────
            // PersonaLoader throws at construction if the persona file is missing or empty.
            // AddDeveloperPersona binds AgentOptions.PersonaPath into the Agent.Runtime PersonaLoader
            // (the host-side "thin DI wrapper" for the library's string-ctor). The
            // IAgentChatClientFactory (AnthropicChatClientFactory) was registered above by
            // AddAgentRuntimeServices; it resolves the API key lazily (on first Create) so an
            // unconfigured dotnet run does not crash.
            builder.Services.AddDeveloperPersona();
            builder.Services.AddSingleton<ITool, ReadFileTool>();
            builder.Services.AddSingleton<ITool, WriteFileTool>();
            builder.Services.AddSingleton<ITool, EditFileTool>();
            builder.Services.AddSingleton<ITool, ListDirectoryTool>();
            builder.Services.AddSingleton<ITool, ShellRunTool>();
            builder.Services.AddSingleton<ITool, CommentOnItemTool>();
            builder.Services.AddSingleton<ITool, CreatePullRequestTool>();
            builder.Services.AddSingleton<IAgentRunner, AnthropicAgentRunner>();

            // Relevance-triage gate (Step-54): one lightweight Anthropic classification call per item,
            // reusing the same chat-client transport as the agent. Singleton (stateless).
            builder.Services.AddSingleton<ITriageService, AnthropicTriageService>();

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

            // ── Agent.Memory stores (Steps 18 / 25; extracted to Agent.Memory in Step-40) ─
            // DaprClient is constructed once via DaprClientBuilder (Dapr.Client 1.17.9); the
            // host owns it (AddAgentMemoryServices' DaprClientStateAdapter depends on it, the
            // same way AddGitHubProjectServices depends on a host-supplied IGitHubTokenProvider).
            // AddAgentMemoryServices then registers the IDaprStateClient adapter plus the
            // IAgentSessionStore (agent-session:{id}) and IAgentMemoryStore (repo-state / task-memory)
            // singletons. AgentId = Environment.MachineName matches the DaprActorTaskStateStore
            // registration above. The MAF providers and the ISummarizer/IMemoryExtractor seams are
            // constructed per run, not via DI.
            builder.Services.AddSingleton<DaprClient>(_ => new DaprClientBuilder().Build());
            builder.Services.AddAgentMemoryServices(Environment.MachineName);

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
