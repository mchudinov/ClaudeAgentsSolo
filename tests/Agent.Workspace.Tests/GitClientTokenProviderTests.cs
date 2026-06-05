using System.Text;
using Agent.Sandbox;
using Agent.Workspace;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Agent.Workspace.Tests;

/// <summary>
/// Pins the <see cref="IGitTokenProvider"/> seam introduced in Step-51: <see cref="GitClient"/>
/// resolves the GitHub token from the host-supplied provider (no longer a baked-in
/// <c>SecretsBundle</c>) and embeds it as git-over-HTTPS Basic auth on the clone/push command line.
/// Uses a substituted <see cref="ICommandSandbox"/> so no real git runs.
/// </summary>
public sealed class GitClientTokenProviderTests
{
    private static readonly TaskWorkspace Ws = new(
        ProjectItemId: "item-1",
        BranchName: "agent/feature",
        RepoRoot: "/tmp/item-1/repo",
        DefaultBranch: "main");

    private static GitClient Build(ICommandSandbox sandbox, IGitTokenProvider tokenProvider) =>
        new(sandbox,
            Options.Create(new DiffScopeLimitOptions()),
            tokenProvider,
            NullLogger<GitClient>.Instance);

    [Fact]
    public async Task CloneAsync_reads_the_token_from_the_provider_and_embeds_it_as_Basic_auth()
    {
        const string token = "ghp_seamtoken1234567890";

        string? capturedCommand = null;
        var sandbox = Substitute.For<ICommandSandbox>();
        sandbox.RunAsync(
                Arg.Do<string>(c => capturedCommand = c),
                Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(new CommandResult(0, "", "", TimeSpan.Zero, false));

        var tokenProvider = Substitute.For<IGitTokenProvider>();
        tokenProvider.GetToken().Returns(token);

        var client = Build(sandbox, tokenProvider);

        await client.CloneAsync(Ws, "https://github.com/org/repo.git", CancellationToken.None);

        tokenProvider.Received().GetToken();
        var expectedBasic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));
        capturedCommand.Should().NotBeNull();
        capturedCommand!.Should().Contain($"Authorization: Basic {expectedBasic}");
    }

    [Fact]
    public async Task CloneAsync_with_an_empty_provider_token_omits_the_auth_header()
    {
        string? capturedCommand = null;
        var sandbox = Substitute.For<ICommandSandbox>();
        sandbox.RunAsync(
                Arg.Do<string>(c => capturedCommand = c),
                Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(new CommandResult(0, "", "", TimeSpan.Zero, false));

        var tokenProvider = Substitute.For<IGitTokenProvider>();
        tokenProvider.GetToken().Returns(string.Empty);

        var client = Build(sandbox, tokenProvider);

        await client.CloneAsync(Ws, "https://github.com/org/repo.git", CancellationToken.None);

        capturedCommand.Should().NotBeNull();
        capturedCommand!.Should().NotContain("http.extraheader");
    }
}
