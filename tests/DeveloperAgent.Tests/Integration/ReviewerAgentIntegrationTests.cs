// NOTE: This test class fetches a real PR via the GitHub REST API and (if the PR passes the
// deterministic pre-checks) calls the real Anthropic API for the persona scan, then SUBMITS a
// review on the fixture PR. It is opt-in: skipped unless all required env vars are set. Point
// it at a throwaway fixture PR on a sandbox repo — it will post an Approve/RequestChanges review.

using DeveloperAgent.Agent;
using DeveloperAgent.Agent.Review;
using DeveloperAgent.Configuration;
using DeveloperAgent.GitHub;
using DeveloperAgent.Sandbox;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeveloperAgent.Tests.Integration;

/// <summary>
/// Integration test for <see cref="ReviewerAgent"/> against a real fixture PR.
/// <para>Required env vars:</para>
/// <list type="bullet">
///   <item><c>GITHUB_INTEGRATION_REPO</c> — <c>owner/repo</c></item>
///   <item><c>GITHUB_INTEGRATION_TOKEN</c> — PAT with <c>repo</c> scope</item>
///   <item><c>REVIEWER_INTEGRATION_PR</c> — fixture PR number to review</item>
///   <item><c>ANTHROPIC_INTEGRATION_KEY</c> — Anthropic API key (for the persona scan)</item>
/// </list>
/// If any variable is absent the test is skipped.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReviewerAgentIntegrationTests
{
    private const string EnvRepo  = "GITHUB_INTEGRATION_REPO";
    private const string EnvToken = "GITHUB_INTEGRATION_TOKEN";
    private const string EnvPr    = "REVIEWER_INTEGRATION_PR";
    private const string EnvKey   = "ANTHROPIC_INTEGRATION_KEY";

    [SkippableFact]
    public async Task Reviewer_reviews_a_fixture_PR_and_posts_a_verdict()
    {
        var reason = EnvironmentSkip.ReasonIfMissing(EnvRepo, EnvToken, EnvPr, EnvKey);
        Skip.If(reason is not null, reason ?? string.Empty);

        var rawRepo   = Environment.GetEnvironmentVariable(EnvRepo)!;
        var ghToken   = Environment.GetEnvironmentVariable(EnvToken)!;
        var prNumber  = int.Parse(Environment.GetEnvironmentVariable(EnvPr)!);
        var anthropicKey = Environment.GetEnvironmentVariable(EnvKey)!;

        var parts = rawRepo.Split('/');
        parts.Length.Should().Be(2, because: $"GITHUB_INTEGRATION_REPO must be 'owner/repo', got '{rawRepo}'");
        var owner = parts[0];
        var repoName = parts[1];

        var githubOptions = Options.Create(new GitHubOptions
        {
            Owner = owner,
            Repository = new RepositoryOptions { Name = repoName, DefaultBranch = "main" },
            Project = new ProjectOptions { Number = 1, OwnerType = "User" },
        });

        // Wire the resilient named HttpClients the Octokit transports + the Anthropic chat
        // client source from (mirrors Program.cs). Permissive allowlist for the live hosts.
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<SandboxOptions>>(Options.Create(new SandboxOptions
        {
            AllowedHosts = new List<string> { "api.github.com", "*.githubusercontent.com", "api.anthropic.com" }
        }));
        services.AddTransient<HostAllowlistHandler>();
        services.AddHttpClient(GitHubHttpClients.Rest)
            .AddHttpMessageHandler<HostAllowlistHandler>().AddStandardResilienceHandler();
        services.AddHttpClient(GitHubHttpClients.GraphQL)
            .AddHttpMessageHandler<HostAllowlistHandler>().AddStandardResilienceHandler();
        services.AddHttpClient(DeveloperAgent.Resilience.HttpClientNames.Anthropic)
            .AddHttpMessageHandler<HostAllowlistHandler>().AddStandardResilienceHandler();
        var provider = services.BuildServiceProvider();
        var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
        var handlerFactory = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        var secrets = new SecretsBundle(AnthropicApiKey: anthropicKey, GitHubToken: ghToken);
        var tokenProvider = new SecretsBundleGitHubTokenProvider(secrets);
        var graphQL = new OctokitGraphQLTransport(githubOptions, tokenProvider, httpClientFactory);
        var rest = new OctokitRestTransport(githubOptions, tokenProvider, handlerFactory);
        var client = new GitHubProjectsClient(graphQL, rest, githubOptions, NullLogger<GitHubProjectsClient>.Instance);
        var gitHub = new GitHubProjectService(client, Options.Create(new ProjectStateNames()));

        var chatClientFactory = new AnthropicChatClientFactory(secrets, httpClientFactory);

        // Load the real reviewer persona from the repo's personas directory.
        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(FindRepoRoot());
        var persona = new ReviewerPersonaLoader(Options.Create(new ReviewerOptions()), env);

        var reviewer = new ReviewerAgent(
            gitHub,
            chatClientFactory,
            persona,
            Options.Create(new AgentOptions { Model = "claude-haiku-4-5" }),
            Options.Create(new ReviewerOptions()),
            NullLogger<ReviewerAgent>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var result = await reviewer.ReviewAsync(prNumber, cts.Token);

        result.Verdict.Should().BeOneOf(ReviewVerdict.Approve, ReviewVerdict.RequestChanges);
        result.Summary.Should().NotBeNullOrWhiteSpace();
    }

    private static string FindRepoRoot()
    {
        // Walk up from the test output dir until a directory containing "personas/reviewer.md" is found.
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "personas", "reviewer.md")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        // Fall back to the output dir; the persona is copied there via the csproj <Content Include>.
        return AppContext.BaseDirectory;
    }
}
