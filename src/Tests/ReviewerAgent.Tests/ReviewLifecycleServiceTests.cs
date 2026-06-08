using Agent.GitHub;
using Agent.Review;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReviewerAgent.Configuration;
using ReviewerAgent.Lifecycle;

namespace ReviewerAgent.Tests;

public sealed class ReviewLifecycleServiceTests
{
    private static ReviewLifecycleService Build(
        IGitHubProjectsClient gitHub, IReviewerAgent reviewer, ReviewPollingOptions options)
    {
        var sp = new ServiceCollection()
            .AddSingleton(gitHub)
            .AddSingleton(reviewer)
            .BuildServiceProvider();
        return new ReviewLifecycleService(sp, Options.Create(options), TimeProvider.System,
            NullLogger<ReviewLifecycleService>.Instance);
    }

    private static IGitHubProjectsClient GitHub(
        IReadOnlyList<OpenPullRequest> open,
        Func<int, IReadOnlyList<string>>? reviewedByPr = null)
    {
        var gh = Substitute.For<IGitHubProjectsClient>();
        gh.ListOpenPullRequestsAsync(Arg.Any<CancellationToken>()).Returns(open);
        gh.GetReviewedHeadShasAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyList<string>)(reviewedByPr?.Invoke(ci.ArgAt<int>(0)) ?? Array.Empty<string>()));
        return gh;
    }

    [Fact]
    public async Task Reviews_an_unreviewed_open_PR()
    {
        var gh = GitHub([new OpenPullRequest(5, "sha-1", IsDraft: false, Author: "dev", HtmlUrl: "u")]);
        var reviewer = Substitute.For<IReviewerAgent>();
        var svc = Build(gh, reviewer, new ReviewPollingOptions { ReviewerLogin = "bot" });

        await svc.ProcessOnceAsync(CancellationToken.None);

        await reviewer.Received(1).ReviewAsync(5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_a_PR_already_reviewed_at_its_current_head()
    {
        var gh = GitHub(
            [new OpenPullRequest(5, "sha-1", IsDraft: false, Author: "dev", HtmlUrl: "u")],
            reviewedByPr: _ => new[] { "sha-1" });
        var reviewer = Substitute.For<IReviewerAgent>();
        var svc = Build(gh, reviewer, new ReviewPollingOptions { ReviewerLogin = "bot" });

        await svc.ProcessOnceAsync(CancellationToken.None);

        await reviewer.DidNotReceive().ReviewAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Re_reviews_when_head_advanced_past_the_reviewed_sha()
    {
        var gh = GitHub(
            [new OpenPullRequest(5, "sha-2", IsDraft: false, Author: "dev", HtmlUrl: "u")],
            reviewedByPr: _ => new[] { "sha-1" });
        var reviewer = Substitute.For<IReviewerAgent>();
        var svc = Build(gh, reviewer, new ReviewPollingOptions { ReviewerLogin = "bot" });

        await svc.ProcessOnceAsync(CancellationToken.None);

        await reviewer.Received(1).ReviewAsync(5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_draft_PRs_when_SkipDrafts_is_true()
    {
        var gh = GitHub([new OpenPullRequest(5, "sha-1", IsDraft: true, Author: "dev", HtmlUrl: "u")]);
        var reviewer = Substitute.For<IReviewerAgent>();
        var svc = Build(gh, reviewer, new ReviewPollingOptions { ReviewerLogin = "bot", SkipDrafts = true });

        await svc.ProcessOnceAsync(CancellationToken.None);

        await reviewer.DidNotReceive().ReviewAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_PRs_whose_author_is_not_in_a_non_empty_allow_list()
    {
        var gh = GitHub([new OpenPullRequest(5, "sha-1", IsDraft: false, Author: "stranger", HtmlUrl: "u")]);
        var reviewer = Substitute.For<IReviewerAgent>();
        var svc = Build(gh, reviewer,
            new ReviewPollingOptions { ReviewerLogin = "bot", AuthorAllowList = new[] { "dev-bot" } });

        await svc.ProcessOnceAsync(CancellationToken.None);

        await reviewer.DidNotReceive().ReviewAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
