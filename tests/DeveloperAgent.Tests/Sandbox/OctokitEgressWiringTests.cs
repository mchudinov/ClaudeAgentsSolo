using DeveloperAgent.Configuration;
using DeveloperAgent.GitHub;
using DeveloperAgent.Resilience;
using DeveloperAgent.Sandbox;
using DeveloperAgent.Workspace;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Tests.Sandbox;

/// <summary>
/// Production-path tests verifying that <see cref="HostAllowlistHandler"/> is
/// actually wired into the Octokit REST and GraphQL transports — a request to a
/// host outside the allowlist throws <see cref="SandboxViolationException"/>
/// from inside the SDK call, BEFORE any HTTP request leaves the process.
/// </summary>
/// <remarks>
/// Step-26 (P2-K) moved both transports onto <see cref="IHttpClientFactory"/> +
/// <see cref="IHttpMessageHandlerFactory"/>, so the egress allowlist is now wired
/// as a message handler on the named HttpClient pipeline (in <c>Program.cs</c>).
/// These tests mirror that wiring in a minimal <see cref="ServiceCollection"/> and
/// resolve the transports through DI, exercising the real production pipeline rather
/// than passing a handler instance directly into a ctor.
/// </remarks>
public sealed class OctokitEgressWiringTests
{
    private static IOptions<GitHubOptions> GitHubOpts(string ownerRepoUrl) =>
        Options.Create(new GitHubOptions
        {
            Owner = "owner",
            Repository = new RepositoryOptions
            {
                Name = "repo",
                Url = ownerRepoUrl,
                DefaultBranch = "main",
            },
            Project = new ProjectOptions { Number = 1, OwnerType = "User" },
            States = new ProjectStateNames(),
        });

    private static SecretsBundle Secrets() =>
        new(AnthropicApiKey: string.Empty, GitHubToken: "ghp_fake_token_for_tests");

    /// <summary>
    /// Builds a minimal service provider with the same named-HttpClient wiring as
    /// <c>Program.cs</c>: <see cref="HostAllowlistHandler"/> first (outer), then the
    /// standard resilience pipeline. <paramref name="allowedHosts"/> deliberately
    /// omits <c>api.github.com</c> so any request the transport makes is denied.
    /// </summary>
    private static ServiceProvider BuildProvider(IEnumerable<string> allowedHosts)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<SandboxOptions>>(
            Options.Create(new SandboxOptions { AllowedHosts = allowedHosts.ToList() }));
        services.AddTransient<HostAllowlistHandler>();
        services.AddHttpClient(HttpClientNames.GitHubRest)
            .AddHttpMessageHandler<HostAllowlistHandler>()
            .AddStandardResilienceHandler();
        services.AddHttpClient(HttpClientNames.GitHubGraphQL)
            .AddHttpMessageHandler<HostAllowlistHandler>()
            .AddStandardResilienceHandler();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task OctokitRestTransport_request_to_denied_host_throws_SandboxViolationException()
    {
        await using var provider = BuildProvider(new[] { "example.com" });
        var handlerFactory = provider.GetRequiredService<IHttpMessageHandlerFactory>();
        var transport = new OctokitRestTransport(GitHubOpts("https://github.com/owner/repo"), Secrets(), handlerFactory);

        var act = async () => await transport.GetPullRequestAsync("owner", "repo", 1, CancellationToken.None);

        await act.Should().ThrowAsync<SandboxViolationException>()
                 .WithMessage("*api.github.com*");
    }

    [Fact]
    public async Task OctokitGraphQLTransport_request_to_denied_host_throws_SandboxViolationException()
    {
        await using var provider = BuildProvider(new[] { "example.com" });
        var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
        var transport = new OctokitGraphQLTransport(GitHubOpts("https://github.com/owner/repo"), Secrets(), httpClientFactory);

        var act = async () => await transport.RunQueryAsync("{ viewer { login } }", null, CancellationToken.None);

        await act.Should().ThrowAsync<SandboxViolationException>()
                 .WithMessage("*api.github.com*");
    }
}
