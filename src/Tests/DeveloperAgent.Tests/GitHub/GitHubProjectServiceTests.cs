using DeveloperAgent.Configuration;
using DeveloperAgent.GitHub;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace DeveloperAgent.Tests.GitHub;

/// <summary>
/// Unit tests for the developer-agent facade <see cref="GitHubProjectService"/>, which maps the
/// four-state <see cref="ProjectState"/> lifecycle onto the agent-neutral
/// <see cref="IGitHubProjectsClient"/> (keyed by status-column name). Deliberately uses non-default
/// column names so a hardcoded "Ready"/"In Progress" mapping would fail these tests.
/// </summary>
public sealed class GitHubProjectServiceTests
{
    private static readonly ProjectStateNames Names = new()
    {
        Backlog = "Parked",
        Ready = "Todo",
        InProgress = "Doing",
        InReview = "Reviewing",
        Done = "Shipped",
    };

    private static GitHubProjectService CreateFacade(IGitHubProjectsClient client)
        => new(client, Options.Create(Names));

    private static IGitHubProjectsClient Client() => Substitute.For<IGitHubProjectsClient>();

    // ── TryGetNextReadyItemAsync ──────────────────────────────────────────────

    [Fact]
    public async Task TryGetNextReadyItemAsync_requests_the_Ready_column_and_maps_status_to_state()
    {
        var client = Client();
        client.TryGetNextItemInStatusAsync("Todo", Arg.Any<CancellationToken>())
            .Returns(new ProjectBoardItem("pvi", "node", 7, "Title", "Body", "Todo"));
        var facade = CreateFacade(client);

        var result = await facade.TryGetNextReadyItemAsync(CancellationToken.None);

        result.Should().NotBeNull();
        result!.State.Should().Be(ProjectState.Ready);
        result.ContentNumber.Should().Be(7);
        await client.Received(1).TryGetNextItemInStatusAsync("Todo", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryGetNextReadyItemAsync_returns_null_when_client_returns_null()
    {
        var client = Client();
        client.TryGetNextItemInStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ProjectBoardItem?)null);
        var facade = CreateFacade(client);

        (await facade.TryGetNextReadyItemAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task TryGetNextReadyItemAsync_excludes_item_whose_status_is_not_a_known_state()
    {
        var client = Client();
        client.TryGetNextItemInStatusAsync("Todo", Arg.Any<CancellationToken>())
            .Returns(new ProjectBoardItem("pvi", "node", 7, "Title", "Body", "Archived"));
        var facade = CreateFacade(client);

        (await facade.TryGetNextReadyItemAsync(CancellationToken.None))
            .Should().BeNull("a status that maps to no ProjectState must be excluded");
    }

    // ── MoveItemAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task MoveItemAsync_maps_both_states_to_their_column_names()
    {
        var client = Client();
        var facade = CreateFacade(client);

        await facade.MoveItemAsync("pvi", ProjectState.Ready, ProjectState.InProgress, CancellationToken.None);

        await client.Received(1).MoveItemAsync("pvi", "Todo", "Doing", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveItemAsync_maps_a_Backlog_target_to_its_column_name()
    {
        // The failure-routing (Step-53) and triage-gate (Step-54) work both MOVE items to Backlog,
        // so ToStatusName must know the Backlog column. Deliberately non-default ("Parked").
        var client = Client();
        var facade = CreateFacade(client);

        await facade.MoveItemAsync("pvi", ProjectState.InProgress, ProjectState.Backlog, CancellationToken.None);

        await client.Received(1).MoveItemAsync("pvi", "Doing", "Parked", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryGetNextReadyItemAsync_excludes_an_item_parked_in_Backlog()
    {
        // Anti-reloop guard (Step-52 → relied on by Step-53): an item sitting in the Backlog column
        // must NEVER be surfaced as a workable item. Backlog is write-only in the lifecycle mapping —
        // ToState maps it to no ProjectState, so even if the Ready query returned one it is dropped.
        var client = Client();
        client.TryGetNextItemInStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ProjectBoardItem("pvi", "node", 7, "Title", "Body", "Parked"));
        var facade = CreateFacade(client);

        (await facade.TryGetNextReadyItemAsync(CancellationToken.None))
            .Should().BeNull("an item parked in Backlog must not be picked up");
    }

    // ── GetInFlightItemsAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task GetInFlightItemsAsync_requests_both_working_columns_and_maps_each_back()
    {
        var client = Client();
        client.GetItemsInStatusesAsync(
                Arg.Is<IReadOnlyCollection<string>>(s => s.SequenceEqual(new[] { "Doing", "Reviewing" })),
                Arg.Any<CancellationToken>())
            .Returns(new List<ProjectBoardItem>
            {
                new("a", "na", 1, "WIP", "", "Doing"),
                new("b", "nb", 2, "Review", "", "Reviewing"),
            });
        var facade = CreateFacade(client);

        var items = await facade.GetInFlightItemsAsync(CancellationToken.None);

        items.Should().HaveCount(2);
        items.Should().ContainSingle(i => i.State == ProjectState.InProgress);
        items.Should().ContainSingle(i => i.State == ProjectState.InReview);
    }

    [Fact]
    public async Task GetInFlightItemsAsync_drops_items_whose_status_is_not_a_known_state()
    {
        var client = Client();
        client.GetItemsInStatusesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ProjectBoardItem>
            {
                new("a", "na", 1, "WIP", "", "Doing"),
                new("c", "nc", 3, "Weird", "", "Archived"),
            });
        var facade = CreateFacade(client);

        var items = await facade.GetInFlightItemsAsync(CancellationToken.None);

        items.Should().ContainSingle().Which.State.Should().Be(ProjectState.InProgress);
    }

    // ── GetReadyItemCountAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetReadyItemCountAsync_requests_the_Ready_column()
    {
        var client = Client();
        client.GetItemCountInStatusAsync("Todo", Arg.Any<CancellationToken>()).Returns(4);
        var facade = CreateFacade(client);

        (await facade.GetReadyItemCountAsync(CancellationToken.None)).Should().Be(4);
        await client.Received(1).GetItemCountInStatusAsync("Todo", Arg.Any<CancellationToken>());
    }

    // ── Pass-throughs ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreatePullRequestAsync_delegates_to_the_client()
    {
        var client = Client();
        var request = new CreatePullRequest("head", "main", "Title", "Body");
        client.CreatePullRequestAsync(request, Arg.Any<CancellationToken>())
            .Returns(new PullRequest(42, "sha", "url"));
        var facade = CreateFacade(client);

        var pr = await facade.CreatePullRequestAsync(request, CancellationToken.None);

        pr.Number.Should().Be(42);
        await client.Received(1).CreatePullRequestAsync(request, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitReviewAsync_delegates_verdict_and_body()
    {
        var client = Client();
        var facade = CreateFacade(client);

        await facade.SubmitReviewAsync(9, ReviewVerdict.Approve, "LGTM", CancellationToken.None);

        await client.Received(1).SubmitReviewAsync(9, ReviewVerdict.Approve, "LGTM", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SquashMergePullRequestAsync_calls_client_with_Squash_method()
    {
        var client = Client();
        client.MergePullRequestAsync(7, MergeMethod.Squash, Arg.Any<CancellationToken>())
            .Returns(MergeOutcome.Merged);
        var facade = CreateFacade(client);

        var outcome = await facade.SquashMergePullRequestAsync(7, CancellationToken.None);

        outcome.Should().Be(MergeOutcome.Merged);
        await client.Received(1).MergePullRequestAsync(7, MergeMethod.Squash, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteBranchAsync_is_a_pass_through_to_the_client()
    {
        var client = Client();
        var facade = CreateFacade(client);

        await facade.DeleteBranchAsync("agent/feature-x", CancellationToken.None);

        await client.Received(1).DeleteBranchAsync("agent/feature-x", Arg.Any<CancellationToken>());
    }
}
