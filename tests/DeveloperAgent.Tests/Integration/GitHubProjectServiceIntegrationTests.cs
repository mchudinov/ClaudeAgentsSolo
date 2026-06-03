using DeveloperAgent.Configuration;
using DeveloperAgent.GitHub;
using DeveloperAgent.Sandbox;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeveloperAgent.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="GitHubProjectService"/> against a real GitHub account.
/// <para>
/// Required env vars:
/// <list type="bullet">
///   <item><c>GITHUB_INTEGRATION_REPO</c> — e.g. <c>owner/integration-sandbox</c></item>
///   <item><c>GITHUB_INTEGRATION_TOKEN</c> — a PAT with <c>repo</c> and <c>project</c> scopes</item>
///   <item><c>GITHUB_INTEGRATION_PROJECT</c> — numeric project number (as shown in the GitHub UI URL)</item>
/// </list>
/// If any variable is absent, every test in this class is skipped.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class GitHubProjectServiceIntegrationTests
{
    private const string EnvRepo    = "GITHUB_INTEGRATION_REPO";
    private const string EnvToken   = "GITHUB_INTEGRATION_TOKEN";
    private const string EnvProject = "GITHUB_INTEGRATION_PROJECT";

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static GitHubProjectService BuildService(
        string owner,
        string repoName,
        int projectNumber,
        string token)
    {
        var options = Options.Create(new GitHubOptions
        {
            Owner = owner,
            Repository = new RepositoryOptions { Name = repoName, DefaultBranch = "main" },
            Project = new ProjectOptions { Number = projectNumber, OwnerType = "User" },
            States = new ProjectStateNames
            {
                Ready      = "Ready",
                InProgress = "In Progress",
                InReview   = "In Review",
                Done       = "Done",
            }
        });

        var tokenProvider = new SecretsBundleGitHubTokenProvider(
            new SecretsBundle(AnthropicApiKey: string.Empty, GitHubToken: token));

        // Step-26 (P2-K) + Step-23 (P2-I) integration: both Octokit transports
        // now source their HttpClient / HttpMessageHandler from IHttpClientFactory +
        // IHttpMessageHandlerFactory, with the egress allowlist (HostAllowlistHandler)
        // composed before the standard resilience pipeline so denied hosts short-circuit
        // before any retry. Mirror the named-client wiring from Program.cs in a minimal
        // ServiceCollection; permissive AllowedHosts so the live api.github.com test passes.
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<SandboxOptions>>(
            Options.Create(new SandboxOptions
            {
                AllowedHosts = new List<string> { "api.github.com", "*.githubusercontent.com" }
            }));
        services.AddTransient<HostAllowlistHandler>();
        services.AddHttpClient(DeveloperAgent.Resilience.HttpClientNames.GitHubRest)
            .AddHttpMessageHandler<HostAllowlistHandler>()
            .AddStandardResilienceHandler();
        services.AddHttpClient(DeveloperAgent.Resilience.HttpClientNames.GitHubGraphQL)
            .AddHttpMessageHandler<HostAllowlistHandler>()
            .AddStandardResilienceHandler();
        var provider = services.BuildServiceProvider();
        var httpClientFactory  = provider.GetRequiredService<IHttpClientFactory>();
        var handlerFactory     = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        var graphQL = new OctokitGraphQLTransport(options, tokenProvider, httpClientFactory);
        var rest    = new OctokitRestTransport(options, tokenProvider, handlerFactory);

        var client = new GitHubProjectsClient(graphQL, rest, options, NullLogger<GitHubProjectsClient>.Instance);
        return new GitHubProjectService(client, Options.Create(options.Value.States));
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task End_to_end_walks_a_project_item_through_the_state_cycle()
    {
        var reason = EnvironmentSkip.ReasonIfMissing(EnvRepo, EnvToken, EnvProject);
        Skip.If(reason is not null, reason ?? string.Empty);

        var rawRepo     = Environment.GetEnvironmentVariable(EnvRepo)!;
        var token       = Environment.GetEnvironmentVariable(EnvToken)!;
        var projectNum  = int.Parse(Environment.GetEnvironmentVariable(EnvProject)!);

        // Parse "owner/repo" from GITHUB_INTEGRATION_REPO
        var parts = rawRepo.Split('/');
        parts.Length.Should().Be(2,
            because: $"GITHUB_INTEGRATION_REPO must be in 'owner/repo' format, got '{rawRepo}'");

        var owner    = parts[0];
        var repoName = parts[1];

        var service = BuildService(owner, repoName, projectNumber: projectNum, token: token);

        // Get a Ready item from the project to exercise state transitions.
        // If no Ready item exists, the test is skipped rather than failed — the sandbox
        // project must have at least one item in the Ready column.
        var item = await service.TryGetNextReadyItemAsync(CancellationToken.None);
        Skip.If(item is null, "No Ready item found in the integration project; populate at least one.");

        var itemId = item.ProjectItemId;

        try
        {
            // Ready → InProgress
            await service.MoveItemAsync(itemId, ProjectState.Ready, ProjectState.InProgress, CancellationToken.None);

            // InProgress → InReview
            await service.MoveItemAsync(itemId, ProjectState.InProgress, ProjectState.InReview, CancellationToken.None);

            // InReview → Done
            await service.MoveItemAsync(itemId, ProjectState.InReview, ProjectState.Done, CancellationToken.None);

            // Verify: the project's Done column contains the item
            // GetInFlightItemsAsync returns InProgress + InReview; the item should NOT be in-flight anymore
            var inFlight = await service.GetInFlightItemsAsync(CancellationToken.None);
            inFlight.Should().NotContain(
                i => i.ProjectItemId == itemId,
                because: "item was moved to Done and should no longer appear in in-flight list");
        }
        finally
        {
            // Cleanup: restore the item to Ready so the test is repeatable.
            // Swallow exceptions here — we don't want cleanup failure to mask a test assertion failure.
            try
            {
                await service.MoveItemAsync(itemId, ProjectState.Done, ProjectState.Ready, CancellationToken.None);
            }
            catch
            {
                // Best-effort; manual cleanup may be required if this fails.
            }
        }
    }
}
