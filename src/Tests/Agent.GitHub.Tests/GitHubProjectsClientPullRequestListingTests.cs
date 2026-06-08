using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agent.GitHub.Tests;

public sealed class GitHubProjectsClientPullRequestListingTests
{
    private static readonly GitHubOptions Options = new()
    {
        Owner = "acme",
        Repository = new RepositoryOptions { Name = "widgets" },
    };

    // NOTE: IGraphQLTransport / IRestTransport are internal — these tests rely on the existing
    // InternalsVisibleTo("Agent.GitHub.Tests"). If the existing tests use a shared fake/fixture
    // for these transports, reuse it instead of Substitute.For here.

    [Fact]
    public async Task ListOpenPullRequestsAsync_maps_transport_dtos()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();
        rest.ListOpenPullRequestsAsync("acme", "widgets", Arg.Any<CancellationToken>())
            .Returns(new List<RestOpenPullRequest>
            {
                new(11, "sha-a", IsDraft: false, Author: "dev-bot", HtmlUrl: "u1"),
                new(12, "sha-b", IsDraft: true,  Author: "human",   HtmlUrl: "u2"),
            });

        var client = new GitHubProjectsClient(graphQL, rest, Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<GitHubProjectsClient>.Instance);

        var result = await client.ListOpenPullRequestsAsync(CancellationToken.None);

        result.Should().HaveCount(2);
        result[0].Should().BeEquivalentTo(new OpenPullRequest(11, "sha-a", false, "dev-bot", "u1"));
        result[1].IsDraft.Should().BeTrue();
    }

    [Fact]
    public async Task GetReviewedHeadShasAsync_returns_distinct_shas_for_that_login_only()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();
        rest.GetPullRequestReviewsAsync("acme", "widgets", 11, Arg.Any<CancellationToken>())
            .Returns(new List<RestPullRequestReview>
            {
                new(1, "reviewer-bot", "APPROVED",          "sha-old", DateTimeOffset.UnixEpoch),
                new(2, "reviewer-bot", "CHANGES_REQUESTED", "sha-old", DateTimeOffset.UnixEpoch.AddMinutes(1)),
                new(3, "reviewer-bot", "APPROVED",          "sha-new", DateTimeOffset.UnixEpoch.AddMinutes(2)),
                new(4, "someone-else",  "APPROVED",         "sha-x",   DateTimeOffset.UnixEpoch.AddMinutes(3)),
            });

        var client = new GitHubProjectsClient(graphQL, rest, Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<GitHubProjectsClient>.Instance);

        var shas = await client.GetReviewedHeadShasAsync(11, "reviewer-bot", CancellationToken.None);

        shas.Should().BeEquivalentTo(new[] { "sha-old", "sha-new" });
    }
}
