// NOTE: This test class calls the real Anthropic API and bills ANTHROPIC_INTEGRATION_KEY
// for approximately 1–3 K tokens per run. It is opt-in: the test is skipped unless the
// environment variable is set. Do not add this test to a default CI pipeline without
// configuring billing limits and secret storage.

using DeveloperAgent.Agent;
using DeveloperAgent.Agent.Tools;
using DeveloperAgent.Configuration;
using DeveloperAgent.GitHub;
using DeveloperAgent.Tests.Sandbox;
using DeveloperAgent.Workspace;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeveloperAgent.Tests.Integration;

/// <summary>
/// Integration test for <see cref="AnthropicAgentRunner"/> against the real Anthropic API.
/// <para>
/// Required env var: <c>ANTHROPIC_INTEGRATION_KEY</c> — an Anthropic API key.
/// If absent, every test in this class is skipped.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class AnthropicAgentRunnerIntegrationTests : IDisposable
{
    private const string EnvKey = "ANTHROPIC_INTEGRATION_KEY";

    private readonly string _tempDir;

    public AnthropicAgentRunnerIntegrationTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "anthropic-int-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; do not fail if delete fails.
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private PersonaLoader MakePersonaLoader()
    {
        var personasDir = Path.Combine(_tempDir, "personas");
        Directory.CreateDirectory(personasDir);
        File.WriteAllText(
            Path.Combine(personasDir, "developer.md"),
            "You are a software developer agent. Implement the requested task, " +
            "then call create_pull_request when done.");

        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);
        return new PersonaLoader("personas/developer.md", env);
    }

    private AnthropicAgentRunner BuildRunner(
        string apiKey,
        string workspaceRoot,
        IGitHubProjectService fakeGitHub)
    {
        var secrets = new SecretsBundle(AnthropicApiKey: apiKey, GitHubToken: string.Empty);

        // Step-26 (P2-K): AnthropicChatClientFactory now sources its HttpClient from
        // IHttpClientFactory so the standard resilience pipeline applies. Spin up a
        // minimal ServiceCollection with the same named-client + AddStandardResilienceHandler
        // wiring Program.cs uses.
        var services = new ServiceCollection();
        services.AddHttpClient(AnthropicHttpClients.ChatClient)
            .AddStandardResilienceHandler();
        var httpClientFactory = services.BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>();

        var chatClientFactory = new AnthropicChatClientFactory(
            new SecretsBundleAnthropicApiKeyProvider(secrets), httpClientFactory);

        var workspaceOpts = Options.Create(new WorkspaceOptions
        {
            RootPath = workspaceRoot,
            AllowedCommands = ProductionSandboxConfig.AllowedCommands,
        });
        var agentOpts = Options.Create(new AgentOptions
        {
            Model             = "claude-haiku-4-5",   // cheapest model for integration test
            Effort            = "low",                // minimal thinking budget
            PersonaPath       = "personas/developer.md",
        });
        var scopeLimits = Options.Create(new ScopeLimitOptions
        {
            MaxModelTurns = 6,                        // bound cost at ~6 model turns
        });

        // Provide the minimal tool set: write + create PR.
        // ShellRun and git tools are intentionally omitted so the agent is limited
        // to file writes and PR creation, keeping the test predictable and cheap.
        var sandboxOpts = Options.Create(new SandboxOptions
        {
            DenyPathPatterns = [],
            SecretFileRegexes = [],
            DeniedCommands = [],
        });
        var denyPolicy = new PathDenyPolicy(sandboxOpts);

        // Stub git client: the create_pull_request MaxPRSize gate calls GetDiffStatsAsync;
        // return a small diff so the gate never blocks in this integration scenario.
        var gitClient = Substitute.For<IGitClient>();
        gitClient.GetDiffStatsAsync(Arg.Any<TaskWorkspace>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(new DiffStats(1, 10));

        ITool[] tools =
        [
            new WriteFileTool(denyPolicy),
            new ReadFileTool(denyPolicy),
            new CreatePullRequestTool(fakeGitHub, gitClient, scopeLimits),
            new CommentOnItemTool(fakeGitHub),
        ];

        return new AnthropicAgentRunner(
            chatClientFactory,
            MakePersonaLoader(),
            agentOpts,
            scopeLimits,
            tools,
            NullLogger<AnthropicAgentRunner>.Instance);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Runner_completes_a_trivial_task_against_the_real_API()
    {
        var reason = EnvironmentSkip.ReasonIfMissing(EnvKey);
        Skip.If(reason is not null, reason ?? string.Empty);

        var apiKey = Environment.GetEnvironmentVariable(EnvKey)!;

        // Set up a fake workspace: a real temp directory that tools can write to.
        var repoRoot = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(repoRoot);

        var expectedPr = new PullRequest(1, "sha-integration", "https://github.com/org/repo/pull/1");

        // Fake GitHub service: CreatePullRequestAsync returns a stub PR.
        var fakeGitHub = Substitute.For<IGitHubProjectService>();
        fakeGitHub
            .CreatePullRequestAsync(Arg.Any<CreatePullRequest>(), Arg.Any<CancellationToken>())
            .Returns(expectedPr);
        fakeGitHub
            .AddItemCommentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var ws = new TaskWorkspace(
            ProjectItemId: "int-agent-1",
            BranchName:    "agent/integration-touch",
            RepoRoot:      repoRoot,
            DefaultBranch: "main");

        var item = new ProjectItem(
            ProjectItemId:  "int-agent-1",
            ContentNodeId:  "content-int-1",
            ContentNumber:  1,
            Title:          "Create integration-touch.txt",
            BodyMarkdown:   "Create a file named 'integration-touch.txt' in the workspace root " +
                            "with content 'hello from integration test'. Then call create_pull_request.",
            State:          ProjectState.InProgress);

        var request = new AgentRunRequest(
            Item:               item,
            Workspace:          ws,
            PriorReviewFeedback: null);

        var runner = BuildRunner(apiKey, _tempDir, fakeGitHub);

        // Act — use a generous timeout so the test doesn't flap on slow API responses.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var result = await runner.RunAsync(request, cts.Token);

        // Assert: the runner completed or hit the hard cap (both are acceptable; the goal
        // is that it didn't throw an unhandled exception or return ApiError).
        result.Outcome.Should().BeOneOf(
            [AgentRunOutcome.Completed, AgentRunOutcome.HardCapReached],
            "the runner should finish the trivial task or hit the cap, not error out");

        if (result.Outcome == AgentRunOutcome.Completed)
        {
            // If the model completed the task, it should have called create_pull_request.
            result.PullRequest.Should().NotBeNull(
                because: "the task description instructs the agent to call create_pull_request");
        }
    }
}
