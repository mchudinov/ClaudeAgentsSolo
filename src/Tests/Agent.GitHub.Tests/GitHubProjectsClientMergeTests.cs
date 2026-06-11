using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agent.GitHub.Tests;

public sealed class GitHubProjectsClientMergeTests
{
    private static GitHubProjectsClient CreateClient(IRestTransport rest) =>
        new(Substitute.For<IGraphQLTransport>(), rest,
            Options.Create(new GitHubOptions
            {
                Owner = "test-org",
                Repository = new RepositoryOptions { Name = "test-repo", DefaultBranch = "main" },
                Project = new ProjectOptions { Number = 1, OwnerType = "Organization" }
            }),
            NullLogger<GitHubProjectsClient>.Instance);

    [Fact]
    public async Task MergePullRequestAsync_returns_AlreadyMerged_without_calling_merge_when_PR_is_merged()
    {
        var rest = Substitute.For<IRestTransport>();
        rest.GetPullRequestAsync("test-org", "test-repo", 7, Arg.Any<CancellationToken>())
            .Returns(new RestPullRequest(7, "sha7", "url", Merged: true, Mergeable: null));

        var client = CreateClient(rest);

        var outcome = await client.MergePullRequestAsync(7, MergeMethod.Squash, CancellationToken.None);

        outcome.Should().Be(MergeOutcome.AlreadyMerged);
        await rest.DidNotReceiveWithAnyArgs()
            .MergePullRequestAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task MergePullRequestAsync_calls_transport_with_squash_and_returns_Merged()
    {
        var rest = Substitute.For<IRestTransport>();
        rest.GetPullRequestAsync("test-org", "test-repo", 7, Arg.Any<CancellationToken>())
            .Returns(new RestPullRequest(7, "sha7", "url", Merged: false, Mergeable: true));
        rest.MergePullRequestAsync("test-org", "test-repo", 7, MergeMethod.Squash, Arg.Any<CancellationToken>())
            .Returns(MergeOutcome.Merged);

        var client = CreateClient(rest);

        var outcome = await client.MergePullRequestAsync(7, MergeMethod.Squash, CancellationToken.None);

        outcome.Should().Be(MergeOutcome.Merged);
        await rest.Received(1).MergePullRequestAsync(
            "test-org", "test-repo", 7, MergeMethod.Squash, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MergePullRequestAsync_returns_NotMergeable_when_transport_reports_it()
    {
        var rest = Substitute.For<IRestTransport>();
        rest.GetPullRequestAsync("test-org", "test-repo", 7, Arg.Any<CancellationToken>())
            .Returns(new RestPullRequest(7, "sha7", "url", Merged: false, Mergeable: false));
        rest.MergePullRequestAsync("test-org", "test-repo", 7, MergeMethod.Squash, Arg.Any<CancellationToken>())
            .Returns(MergeOutcome.NotMergeable);

        var client = CreateClient(rest);

        var outcome = await client.MergePullRequestAsync(7, MergeMethod.Squash, CancellationToken.None);

        outcome.Should().Be(MergeOutcome.NotMergeable);
    }

    [Fact]
    public async Task DeleteBranchAsync_delegates_to_transport_with_configured_repo()
    {
        var rest = Substitute.For<IRestTransport>();
        var client = CreateClient(rest);

        await client.DeleteBranchAsync("agent/feature-x", CancellationToken.None);

        await rest.Received(1).DeleteBranchAsync(
            "test-org", "test-repo", "agent/feature-x", Arg.Any<CancellationToken>());
    }
}
