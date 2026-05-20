using System.Text.Json;
using DeveloperAgent.Configuration;
using DeveloperAgent.GitHub;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace DeveloperAgent.Tests.GitHub;

/// <summary>
/// Unit tests for <see cref="GitHubProjectService"/> against fake transports.
/// No real network calls are made.
/// </summary>
public sealed class GitHubProjectServiceTests
{
    // ── Test factory helpers ─────────────────────────────────────────────────

    private static GitHubProjectService CreateService(
        IGraphQLTransport graphQL,
        IRestTransport rest,
        GitHubOptions? options = null)
    {
        options ??= DefaultOptions();
        return new GitHubProjectService(
            graphQL,
            rest,
            Options.Create(options),
            NullLogger<GitHubProjectService>.Instance);
    }

    private static GitHubOptions DefaultOptions() => new()
    {
        Owner = "test-org",
        Repository = new RepositoryOptions { Name = "test-repo", DefaultBranch = "main" },
        Project = new ProjectOptions { Number = 1, OwnerType = "Organization" },
        States = new ProjectStateNames
        {
            Ready = "Ready",
            InProgress = "In Progress",
            InReview = "In Review",
            Done = "Done"
        }
    };

    // Builds the JSON that GetOptionIdsAsync parses
    private static JsonElement BuildOptionIdsResponse(
        string ownerType = "Organization",
        (string name, string id)[]? options = null)
    {
        options ??= [
            ("Ready", "opt-ready"),
            ("In Progress", "opt-inprogress"),
            ("In Review", "opt-inreview"),
            ("Done", "opt-done")
        ];

        var optsJson = string.Join(",", options.Select(o => $"{{\"name\":\"{o.name}\",\"id\":\"{o.id}\"}}"));
        var json = ownerType == "Organization"
            ? $$"""
                {
                  "data": {
                    "organization": {
                      "projectV2": {
                        "fields": {
                          "nodes": [
                            {
                              "name": "Status",
                              "options": [{{optsJson}}]
                            }
                          ]
                        }
                      }
                    }
                  }
                }
                """
            : $$"""
                {
                  "data": {
                    "user": {
                      "projectV2": {
                        "fields": {
                          "nodes": [
                            {
                              "name": "Status",
                              "options": [{{optsJson}}]
                            }
                          ]
                        }
                      }
                    }
                  }
                }
                """;

        return JsonDocument.Parse(json).RootElement;
    }

    private static JsonElement BuildProjectNodeIdResponse(string ownerType = "Organization")
    {
        var json = ownerType == "Organization"
            ? """{"data":{"organization":{"projectV2":{"id":"project-node-id"}}}}"""
            : """{"data":{"user":{"projectV2":{"id":"project-node-id"}}}}""";
        return JsonDocument.Parse(json).RootElement;
    }

    private static JsonElement BuildStatusFieldIdResponse(string ownerType = "Organization")
    {
        var json = ownerType == "Organization"
            ? """{"data":{"organization":{"projectV2":{"fields":{"nodes":[{"id":"field-node-id","name":"Status"}]}}}}}"""
            : """{"data":{"user":{"projectV2":{"fields":{"nodes":[{"id":"field-node-id","name":"Status"}]}}}}}""";
        return JsonDocument.Parse(json).RootElement;
    }

    private static JsonElement BuildItemsResponse(
        string ownerType = "Organization",
        bool includeIssue = true,
        bool includeDraftIssue = false)
    {
        var nodes = new List<string>();
        if (includeIssue)
        {
            nodes.Add("""
                {
                  "id": "pvitem-1",
                  "fieldValues": {
                    "nodes": [
                      {
                        "optionId": "opt-ready",
                        "field": { "name": "Status" }
                      }
                    ]
                  },
                  "content": {
                    "__typename": "Issue",
                    "id": "issue-node-1",
                    "number": 42,
                    "title": "Implement feature X",
                    "body": "Details here"
                  }
                }
                """);
        }
        if (includeDraftIssue)
        {
            nodes.Add("""
                {
                  "id": "pvitem-2",
                  "fieldValues": {
                    "nodes": [
                      {
                        "optionId": "opt-ready",
                        "field": { "name": "Status" }
                      }
                    ]
                  },
                  "content": {
                    "__typename": "DraftIssue",
                    "id": "draft-node-1",
                    "title": "Draft task",
                    "body": "Draft body"
                  }
                }
                """);
        }

        var nodesJson = string.Join(",", nodes);
        var json = ownerType == "Organization"
            ? "{\"data\":{\"organization\":{\"projectV2\":{\"items\":{\"nodes\":[" + nodesJson + "]}}}}}"
            : "{\"data\":{\"user\":{\"projectV2\":{\"items\":{\"nodes\":[" + nodesJson + "]}}}}}";

        return JsonDocument.Parse(json).RootElement;
    }

    // ── §D.1: Option-ID cache ─────────────────────────────────────────────────

    [Fact]
    public async Task GetOptionIds_cache_hit_on_second_call_does_not_re_query_transport()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        // Option-ID fetch query (contains "options")
        graphQL.RunQueryAsync(
                   Arg.Is<string>(s => s.Contains("options")),
                   Arg.Any<Dictionary<string, object>?>(),
                   Arg.Any<CancellationToken>())
               .Returns(BuildOptionIdsResponse());

        // Items query returns empty list
        graphQL.RunQueryAsync(
                   Arg.Is<string>(s => s.Contains("items")),
                   Arg.Any<Dictionary<string, object>?>(),
                   Arg.Any<CancellationToken>())
               .Returns(JsonDocument.Parse("""{"data":{"organization":{"projectV2":{"items":{"nodes":[]}}}}}""").RootElement);

        var svc = CreateService(graphQL, rest);

        // First call triggers option-ID fetch + items query
        await svc.TryGetNextReadyItemAsync(CancellationToken.None);

        // Second call — option IDs should be cached; only the items query fires again
        await svc.TryGetNextReadyItemAsync(CancellationToken.None);

        // The option-IDs query (contains "options") must fire exactly once
        var optionIdQueryCalls = graphQL.ReceivedCalls()
            .Count(c => ((string)c.GetArguments()[0]!).Contains("options"));

        optionIdQueryCalls.Should().Be(1, "option IDs should be fetched only once across both TryGetNextReady calls");
    }

    // ── §D.2: MoveItemAsync no-op ─────────────────────────────────────────────

    [Fact]
    public async Task MoveItemAsync_same_current_and_target_state_skips_mutation()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        var svc = CreateService(graphQL, rest);

        // current == target → mutation transport must NOT be called
        await svc.MoveItemAsync("pvitem-1", ProjectState.Ready, ProjectState.Ready, CancellationToken.None);

        await graphQL.DidNotReceive().RunMutationAsync(
            Arg.Any<string>(),
            Arg.Any<Dictionary<string, object>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveItemAsync_different_states_calls_mutation_with_correct_optionId()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        // Option-ID query
        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("options")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(BuildOptionIdsResponse());
        // Status field ID query
        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("ProjectV2SingleSelectField") && !s.Contains("options")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(BuildStatusFieldIdResponse());
        // Project node ID query
        graphQL.RunQueryAsync(Arg.Is<string>(s => !s.Contains("fields") && !s.Contains("options") && !s.Contains("items")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(BuildProjectNodeIdResponse());
        // Mutation
        graphQL.RunMutationAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(JsonDocument.Parse("{}").RootElement);

        var svc = CreateService(graphQL, rest);

        await svc.MoveItemAsync("pvitem-1", ProjectState.Ready, ProjectState.InProgress, CancellationToken.None);

        // Mutation should be called once with the InProgress option ID
        await graphQL.Received(1).RunMutationAsync(
            Arg.Any<string>(),
            Arg.Is<Dictionary<string, object>?>(d => d != null && d["optionId"].Equals("opt-inprogress")),
            Arg.Any<CancellationToken>());
    }

    // ── §D.3: CreatePullRequestAsync 422 handling ─────────────────────────────

    [Fact]
    public async Task CreatePullRequestAsync_on_422_already_exists_returns_existing_PR()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        var request = new CreatePullRequest("feature/test", "main", "Add feature", "## Summary\nTest");

        // First call returns AlreadyExists
        rest.CreatePullRequestAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(new RestCreatePullRequestResult(true, null));

        // Follow-up GET returns the existing PR
        var existingPr = new RestPullRequest(17, "abc123", "https://github.com/org/repo/pull/17", false);
        rest.FindOpenPullRequestByHeadAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(existingPr);

        var svc = CreateService(graphQL, rest);

        var result = await svc.CreatePullRequestAsync(request, CancellationToken.None);

        result.Number.Should().Be(17);
        result.HeadSha.Should().Be("abc123");
        result.HtmlUrl.Should().Be("https://github.com/org/repo/pull/17");

        // Verify POST was called once, then fallback GET was called once
        await rest.Received(1).CreatePullRequestAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await rest.Received(1).FindOpenPullRequestByHeadAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            request.HeadBranch, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreatePullRequestAsync_success_returns_new_PR()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        var newPr = new RestPullRequest(99, "sha999", "https://github.com/org/repo/pull/99", false);
        rest.CreatePullRequestAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(new RestCreatePullRequestResult(false, newPr));

        var svc = CreateService(graphQL, rest);

        var result = await svc.CreatePullRequestAsync(
            new CreatePullRequest("feature", "main", "Title", "Body"),
            CancellationToken.None);

        result.Number.Should().Be(99);
        result.HeadSha.Should().Be("sha999");
    }

    // ── §D.4: GetPullRequestStatusAsync ──────────────────────────────────────

    [Fact]
    public async Task GetPullRequestStatusAsync_all_green_approved_merged_returns_all_true()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        rest.GetPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), 5, Arg.Any<CancellationToken>())
            .Returns(new RestPullRequest(5, "sha-abc", "https://...", true));

        rest.GetPullRequestReviewsAsync(Arg.Any<string>(), Arg.Any<string>(), 5, Arg.Any<CancellationToken>())
            .Returns(new List<RestPullRequestReview>
            {
                new(1, "reviewer1", "APPROVED", DateTimeOffset.UtcNow)
            });

        rest.GetCheckRunsAsync(Arg.Any<string>(), Arg.Any<string>(), "sha-abc", Arg.Any<CancellationToken>())
            .Returns(new List<RestCheckRun>
            {
                new(1, "completed", "success"),
                new(2, "completed", "neutral"),
                new(3, "completed", "skipped")
            });

        var svc = CreateService(graphQL, rest);

        var status = await svc.GetPullRequestStatusAsync(5, CancellationToken.None);

        status.Number.Should().Be(5);
        status.Review.Should().Be(PullRequestReviewState.Approved);
        status.ChecksGreen.Should().BeTrue();
        status.Merged.Should().BeTrue();
        status.HeadSha.Should().Be("sha-abc");
    }

    [Fact]
    public async Task GetPullRequestStatusAsync_one_check_failure_sets_ChecksGreen_false()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        rest.GetPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), 5, Arg.Any<CancellationToken>())
            .Returns(new RestPullRequest(5, "sha-abc", "https://...", false));

        rest.GetPullRequestReviewsAsync(Arg.Any<string>(), Arg.Any<string>(), 5, Arg.Any<CancellationToken>())
            .Returns(new List<RestPullRequestReview>());

        rest.GetCheckRunsAsync(Arg.Any<string>(), Arg.Any<string>(), "sha-abc", Arg.Any<CancellationToken>())
            .Returns(new List<RestCheckRun>
            {
                new(1, "completed", "success"),
                new(2, "completed", "failure")  // <-- failure
            });

        var svc = CreateService(graphQL, rest);

        var status = await svc.GetPullRequestStatusAsync(5, CancellationToken.None);

        status.ChecksGreen.Should().BeFalse();
    }

    [Fact]
    public async Task GetPullRequestStatusAsync_latest_review_ChangesRequested_returns_ChangesRequested()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        rest.GetPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), 7, Arg.Any<CancellationToken>())
            .Returns(new RestPullRequest(7, "sha-xyz", "https://...", false));

        var t = DateTimeOffset.UtcNow;
        rest.GetPullRequestReviewsAsync(Arg.Any<string>(), Arg.Any<string>(), 7, Arg.Any<CancellationToken>())
            .Returns(new List<RestPullRequestReview>
            {
                new(1, "reviewer1", "APPROVED", t.AddMinutes(-5)),
                new(2, "reviewer1", "CHANGES_REQUESTED", t) // latest overrides
            });

        rest.GetCheckRunsAsync(Arg.Any<string>(), Arg.Any<string>(), "sha-xyz", Arg.Any<CancellationToken>())
            .Returns(new List<RestCheckRun>());

        var svc = CreateService(graphQL, rest);

        var status = await svc.GetPullRequestStatusAsync(7, CancellationToken.None);

        status.Review.Should().Be(PullRequestReviewState.ChangesRequested);
    }

    [Fact]
    public async Task GetPullRequestStatusAsync_in_progress_check_returns_ChecksGreen_false()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        rest.GetPullRequestAsync(Arg.Any<string>(), Arg.Any<string>(), 3, Arg.Any<CancellationToken>())
            .Returns(new RestPullRequest(3, "sha-def", "https://...", false));

        rest.GetPullRequestReviewsAsync(Arg.Any<string>(), Arg.Any<string>(), 3, Arg.Any<CancellationToken>())
            .Returns(new List<RestPullRequestReview>());

        rest.GetCheckRunsAsync(Arg.Any<string>(), Arg.Any<string>(), "sha-def", Arg.Any<CancellationToken>())
            .Returns(new List<RestCheckRun>
            {
                new(1, "in_progress", null)  // not yet completed
            });

        var svc = CreateService(graphQL, rest);

        var status = await svc.GetPullRequestStatusAsync(3, CancellationToken.None);

        status.ChecksGreen.Should().BeFalse();
    }

    // ── §D.5: GetReviewFeedbackSinceAsync ─────────────────────────────────────

    [Fact]
    public async Task GetReviewFeedbackSinceAsync_filters_old_comments()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        var since = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);

        rest.GetPullRequestReviewCommentsAsync(Arg.Any<string>(), Arg.Any<string>(), 1, Arg.Any<CancellationToken>())
            .Returns(new List<RestPullRequestReviewComment>
            {
                new(1, "reviewer", "Old comment", since.AddHours(-1), null, null), // before cursor
                new(2, "reviewer", "New comment", since.AddHours(1), null, null),  // after cursor
            });

        rest.GetIssueCommentsAsync(Arg.Any<string>(), Arg.Any<string>(), 1, Arg.Any<CancellationToken>())
            .Returns(new List<RestIssueComment>());

        var svc = CreateService(graphQL, rest);

        var feedback = await svc.GetReviewFeedbackSinceAsync(1, since, CancellationToken.None);

        feedback.Should().NotBeNullOrEmpty();
        feedback.Should().Contain("New comment");
        feedback.Should().NotContain("Old comment");
    }

    [Fact]
    public async Task GetReviewFeedbackSinceAsync_orders_chronologically()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        var since = DateTimeOffset.UtcNow.AddDays(-1);

        var t1 = since.AddHours(1);
        var t2 = since.AddHours(3);
        var t3 = since.AddHours(2);

        rest.GetPullRequestReviewCommentsAsync(Arg.Any<string>(), Arg.Any<string>(), 1, Arg.Any<CancellationToken>())
            .Returns(new List<RestPullRequestReviewComment>
            {
                new(1, "reviewer", "Comment A", t1, "src/Foo.cs", 10),
                new(3, "reviewer", "Comment C", t3, "src/Bar.cs", 5),
            });

        rest.GetIssueCommentsAsync(Arg.Any<string>(), Arg.Any<string>(), 1, Arg.Any<CancellationToken>())
            .Returns(new List<RestIssueComment>
            {
                new(2, "reviewer", "Comment B", t2),
            });

        var svc = CreateService(graphQL, rest);

        var feedback = await svc.GetReviewFeedbackSinceAsync(1, since, CancellationToken.None);

        // Order: t1 (A), t3 (C), t2 (B) → t1 < t3 < t2 is wrong — t1 < t3 < t2 → A, C, B
        // Sorted: t1 (A) < t3 (C) < t2 (B)? No: t1 + 1h, t3 = t1 + 2h, t2 = t1 + 2h... wait
        // t1 = since+1h, t2 = since+3h, t3 = since+2h → sorted: t1, t3, t2 → A, C, B
        var aIdx = feedback.IndexOf("Comment A", StringComparison.Ordinal);
        var bIdx = feedback.IndexOf("Comment B", StringComparison.Ordinal);
        var cIdx = feedback.IndexOf("Comment C", StringComparison.Ordinal);

        aIdx.Should().BeLessThan(cIdx, "Comment A (t1) should come before Comment C (t3)");
        cIdx.Should().BeLessThan(bIdx, "Comment C (t3) should come before Comment B (t2)");
    }

    [Fact]
    public async Task GetReviewFeedbackSinceAsync_review_comment_includes_file_path_and_line()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        var since = DateTimeOffset.UtcNow.AddDays(-1);

        rest.GetPullRequestReviewCommentsAsync(Arg.Any<string>(), Arg.Any<string>(), 1, Arg.Any<CancellationToken>())
            .Returns(new List<RestPullRequestReviewComment>
            {
                new(1, "reviewer", "Needs refactor", since.AddHours(1), "src/Foo.cs", 42)
            });

        rest.GetIssueCommentsAsync(Arg.Any<string>(), Arg.Any<string>(), 1, Arg.Any<CancellationToken>())
            .Returns(new List<RestIssueComment>());

        var svc = CreateService(graphQL, rest);

        var feedback = await svc.GetReviewFeedbackSinceAsync(1, since, CancellationToken.None);

        feedback.Should().Contain("file src/Foo.cs:42");
    }

    [Fact]
    public async Task GetReviewFeedbackSinceAsync_issue_comment_omits_file_marker()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        var since = DateTimeOffset.UtcNow.AddDays(-1);

        rest.GetPullRequestReviewCommentsAsync(Arg.Any<string>(), Arg.Any<string>(), 1, Arg.Any<CancellationToken>())
            .Returns(new List<RestPullRequestReviewComment>());

        rest.GetIssueCommentsAsync(Arg.Any<string>(), Arg.Any<string>(), 1, Arg.Any<CancellationToken>())
            .Returns(new List<RestIssueComment>
            {
                new(1, "human-reviewer", "LGTM overall", since.AddHours(1))
            });

        var svc = CreateService(graphQL, rest);

        var feedback = await svc.GetReviewFeedbackSinceAsync(1, since, CancellationToken.None);

        feedback.Should().Contain("LGTM overall");
        feedback.Should().NotContain(" — file ");
    }

    [Fact]
    public async Task GetReviewFeedbackSinceAsync_no_newer_comments_returns_empty_string()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        var since = DateTimeOffset.UtcNow.AddHours(1); // future cursor

        rest.GetPullRequestReviewCommentsAsync(Arg.Any<string>(), Arg.Any<string>(), 1, Arg.Any<CancellationToken>())
            .Returns(new List<RestPullRequestReviewComment>
            {
                new(1, "reviewer", "Old comment", DateTimeOffset.UtcNow, null, null)
            });

        rest.GetIssueCommentsAsync(Arg.Any<string>(), Arg.Any<string>(), 1, Arg.Any<CancellationToken>())
            .Returns(new List<RestIssueComment>());

        var svc = CreateService(graphQL, rest);

        var feedback = await svc.GetReviewFeedbackSinceAsync(1, since, CancellationToken.None);

        feedback.Should().BeEmpty();
    }

    // ── §D.6: TryGetNextReadyItemAsync ────────────────────────────────────────

    [Fact]
    public async Task TryGetNextReadyItemAsync_empty_project_returns_null()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("options")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(BuildOptionIdsResponse());

        // Items query returns empty
        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("items")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(JsonDocument.Parse("""{"data":{"organization":{"projectV2":{"items":{"nodes":[]}}}}}""").RootElement);

        var svc = CreateService(graphQL, rest);

        var result = await svc.TryGetNextReadyItemAsync(CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TryGetNextReadyItemAsync_one_Issue_one_DraftIssue_returns_the_Issue()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("options")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(BuildOptionIdsResponse());

        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("items")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(BuildItemsResponse(includeIssue: true, includeDraftIssue: true));

        var svc = CreateService(graphQL, rest);

        var result = await svc.TryGetNextReadyItemAsync(CancellationToken.None);

        result.Should().NotBeNull();
        result!.ContentNumber.Should().Be(42);
        result.Title.Should().Be("Implement feature X");
    }

    // ── §D.7: GetInFlightItemsAsync ───────────────────────────────────────────

    [Fact]
    public async Task GetInFlightItemsAsync_returns_items_from_both_InProgress_and_InReview()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("options")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(BuildOptionIdsResponse());

        // Return one issue per items query call (InProgress + InReview = 2 calls)
        var call = 0;
        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("items")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(_ =>
               {
                   call++;
                   return BuildItemsResponse(includeIssue: true, includeDraftIssue: false);
               });

        var svc = CreateService(graphQL, rest);

        var items = await svc.GetInFlightItemsAsync(CancellationToken.None);

        // One item per state × 2 states = 2 total
        items.Should().HaveCount(2);
    }

    // ── §D.8: AddItemCommentAsync ─────────────────────────────────────────────

    [Fact]
    public async Task AddItemCommentAsync_invokes_mutation_with_correct_subjectId_and_body()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        graphQL.RunMutationAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(JsonDocument.Parse("{}").RootElement);

        var svc = CreateService(graphQL, rest);

        await svc.AddItemCommentAsync("issue-node-xyz", "Implementation plan: step 1", CancellationToken.None);

        await graphQL.Received(1).RunMutationAsync(
            Arg.Is<string>(s => s.Contains("addComment")),
            Arg.Is<Dictionary<string, object>?>(d =>
                d != null &&
                d["subjectId"].Equals("issue-node-xyz") &&
                d["body"].Equals("Implementation plan: step 1")),
            Arg.Any<CancellationToken>());
    }
}
