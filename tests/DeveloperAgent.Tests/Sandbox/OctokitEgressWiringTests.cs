using DeveloperAgent.Configuration;
using DeveloperAgent.GitHub;
using DeveloperAgent.Sandbox;
using DeveloperAgent.Workspace;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Tests.Sandbox;

/// <summary>
/// Production-path tests verifying that <see cref="HostAllowlistHandler"/> is
/// actually wired into the Octokit REST and GraphQL transports — a request to a
/// host outside the allowlist throws <see cref="SandboxViolationException"/>
/// from inside the SDK call, BEFORE any HTTP request leaves the process.
/// </summary>
/// <remarks>
/// These tests run offline. The denied host name is a non-routable RFC 5737
/// example domain — even if the egress handler regressed, the SDK would still
/// fail (with a DNS or connect error) rather than reach the public internet.
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

    [Fact]
    public async Task OctokitRestTransport_request_to_denied_host_throws_SandboxViolationException()
    {
        // AllowedHosts deliberately omits api.github.com — the transport will
        // still try to call it, and the handler must short-circuit.
        var egress = new HostAllowlistHandler(["example.com"]);
        var transport = new OctokitRestTransport(GitHubOpts("https://github.com/owner/repo"), Secrets(), egress);

        var act = async () => await transport.GetPullRequestAsync("owner", "repo", 1, CancellationToken.None);

        await act.Should().ThrowAsync<SandboxViolationException>()
                 .WithMessage("*api.github.com*");
    }

    [Fact]
    public async Task OctokitGraphQLTransport_request_to_denied_host_throws_SandboxViolationException()
    {
        var egress = new HostAllowlistHandler(["example.com"]);
        var transport = new OctokitGraphQLTransport(GitHubOpts("https://github.com/owner/repo"), Secrets(), egress);

        var act = async () => await transport.RunQueryAsync("{ viewer { login } }", null, CancellationToken.None);

        await act.Should().ThrowAsync<SandboxViolationException>()
                 .WithMessage("*api.github.com*");
    }
}
