using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Agent.GitHub.Tests;

/// <summary>
/// Unit tests for <see cref="GitHubProjectsClient"/> against fake transports.
/// No real network calls are made.
/// </summary>
public sealed class GitHubProjectsClientTests
{
    // ── Test factory helpers ─────────────────────────────────────────────────

    private static GitHubProjectsClient CreateClient(
        IGraphQLTransport graphQL,
        IRestTransport rest,
        GitHubOptions? options = null,
        ILogger<GitHubProjectsClient>? logger = null)
    {
        options ??= DefaultOptions();
        return new GitHubProjectsClient(
            graphQL,
            rest,
            Options.Create(options),
            logger ?? NullLogger<GitHubProjectsClient>.Instance);
    }

    private static GitHubOptions DefaultOptions() => new()
    {
        Owner = "test-org",
        Repository = new RepositoryOptions { Name = "test-repo", DefaultBranch = "main" },
        Project = new ProjectOptions { Number = 1, OwnerType = "Organization" }
    };

    // Builds the JSON that FetchOptionLookupAsync parses.
    // The Status field node now includes "id" (the field's own node ID, used for mutations)
    // and "options" (each option's id and name for the forward/reverse maps).
    private static JsonElement BuildOptionIdsResponse(
        string ownerType = "Organization",
        (string name, string id)[]? options = null,
        string statusFieldNodeId = "field-node-id")
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
                              "id": "{{statusFieldNodeId}}",
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
                              "id": "{{statusFieldNodeId}}",
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
        bool includeDraftIssue = false,
        string issueOptionId = "opt-ready",
        int issueNumber = 42,
        string issueTitle = "Implement feature X")
    {
        var nodes = new List<string>();
        if (includeIssue)
        {
            nodes.Add($$"""
                {
                  "id": "pvitem-1",
                  "fieldValues": {
                    "nodes": [
                      {
                        "optionId": "{{issueOptionId}}",
                        "field": { "name": "Status" }
                      }
                    ]
                  },
                  "content": {
                    "__typename": "Issue",
                    "id": "issue-node-1",
                    "number": {{issueNumber}},
                    "title": "{{issueTitle}}",
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

        var svc = CreateClient(graphQL, rest);

        // First call triggers option-ID fetch + items query
        await svc.TryGetNextItemInStatusAsync("Ready", CancellationToken.None);

        // Second call — option IDs should be cached; only the items query fires again
        await svc.TryGetNextItemInStatusAsync("Ready", CancellationToken.None);

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

        var svc = CreateClient(graphQL, rest);

        // current == target → mutation transport must NOT be called
        await svc.MoveItemAsync("pvitem-1", "Ready", "Ready", CancellationToken.None);

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

        // Single consolidated option-lookup query (includes field id + options)
        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("options")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(BuildOptionIdsResponse());
        // Project node ID query (separate — needed for the mutation projectId variable)
        graphQL.RunQueryAsync(Arg.Is<string>(s => !s.Contains("fields") && !s.Contains("options") && !s.Contains("items")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(BuildProjectNodeIdResponse());
        // Mutation
        graphQL.RunMutationAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(JsonDocument.Parse("{}").RootElement);

        var svc = CreateClient(graphQL, rest);

        await svc.MoveItemAsync("pvitem-1", "Ready", "In Progress", CancellationToken.None);

        // Mutation should be called once with the InProgress option ID and field node ID
        await graphQL.Received(1).RunMutationAsync(
            Arg.Any<string>(),
            Arg.Is<Dictionary<string, object>?>(d =>
                d != null &&
                d["optionId"].Equals("opt-inprogress") &&
                d["fieldId"].Equals("field-node-id")),
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

        var svc = CreateClient(graphQL, rest);

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

        var svc = CreateClient(graphQL, rest);

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
                new(1, "reviewer1", "APPROVED", "", DateTimeOffset.UtcNow)
            });

        rest.GetCheckRunsAsync(Arg.Any<string>(), Arg.Any<string>(), "sha-abc", Arg.Any<CancellationToken>())
            .Returns(new List<RestCheckRun>
            {
                new(1, "completed", "success"),
                new(2, "completed", "neutral"),
                new(3, "completed", "skipped")
            });

        var svc = CreateClient(graphQL, rest);

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

        var svc = CreateClient(graphQL, rest);

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
                new(1, "reviewer1", "APPROVED", "", t.AddMinutes(-5)),
                new(2, "reviewer1", "CHANGES_REQUESTED", "", t) // latest overrides
            });

        rest.GetCheckRunsAsync(Arg.Any<string>(), Arg.Any<string>(), "sha-xyz", Arg.Any<CancellationToken>())
            .Returns(new List<RestCheckRun>());

        var svc = CreateClient(graphQL, rest);

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

        var svc = CreateClient(graphQL, rest);

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

        var svc = CreateClient(graphQL, rest);

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

        var svc = CreateClient(graphQL, rest);

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

        var svc = CreateClient(graphQL, rest);

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

        var svc = CreateClient(graphQL, rest);

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

        var svc = CreateClient(graphQL, rest);

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

        var svc = CreateClient(graphQL, rest);

        var result = await svc.TryGetNextItemInStatusAsync("Ready", CancellationToken.None);

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

        var svc = CreateClient(graphQL, rest);

        var result = await svc.TryGetNextItemInStatusAsync("Ready", CancellationToken.None);

        result.Should().NotBeNull();
        result!.ContentNumber.Should().Be(42);
        result.Title.Should().Be("Implement feature X");
    }

    // ── §D.6b: DraftIssue pickup (regression: drafts were silently skipped) ────

    [Fact]
    public async Task TryGetNextReadyItemAsync_returns_DraftIssue_in_Ready_column()
    {
        // Reproduces the bug: a project whose only Ready item is a DraftIssue.
        // Previously QueryProjectItemsAsync skipped all DraftIssue content, so this
        // returned null. The draft must now surface with ContentNumber 0 (drafts
        // have no number) and State Ready.
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("options")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(BuildOptionIdsResponse());

        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("items")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(BuildItemsResponse(includeIssue: false, includeDraftIssue: true));

        var svc = CreateClient(graphQL, rest);

        var result = await svc.TryGetNextItemInStatusAsync("Ready", CancellationToken.None);

        result.Should().NotBeNull("a DraftIssue in the Ready column must be picked up");
        result!.Title.Should().Be("Draft task");
        result.BodyMarkdown.Should().Be("Draft body");
        result.ContentNumber.Should().Be(0, "draft issues have no number; the service defaults to 0");
        result.Status.Should().Be("Ready");
    }

    [Fact]
    public async Task TryGetNextReadyItemAsync_real_Issue_still_returned_with_its_number()
    {
        // Regression guard: non-draft handling is unchanged — a real Issue in the
        // Ready column keeps its repository number.
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("options")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(BuildOptionIdsResponse());

        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("items")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(BuildItemsResponse(includeIssue: true, includeDraftIssue: false, issueNumber: 7, issueTitle: "Real issue task"));

        var svc = CreateClient(graphQL, rest);

        var result = await svc.TryGetNextItemInStatusAsync("Ready", CancellationToken.None);

        result.Should().NotBeNull();
        result!.ContentNumber.Should().Be(7, "real issues carry their repository number through unchanged");
        result.Title.Should().Be("Real issue task");
        result.Status.Should().Be("Ready");
    }

    [Fact]
    public async Task TryGetNextReadyItemAsync_excludes_DraftIssue_not_in_Ready_column()
    {
        // A DraftIssue tagged with a non-Ready option (Backlog) must not be surfaced.
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("options")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(BuildOptionIdsResponse(options:
               [
                   ("Ready", "opt-ready"),
                   ("In Progress", "opt-inprogress"),
                   ("In Review", "opt-inreview"),
                   ("Done", "opt-done"),
                   ("Backlog", "opt-backlog")
               ]));

        var backlogDraftJson = """
            {
              "data": {
                "organization": {
                  "projectV2": {
                    "items": {
                      "nodes": [
                        {
                          "id": "pvitem-draft-backlog",
                          "fieldValues": {
                            "nodes": [
                              { "optionId": "opt-backlog", "field": { "name": "Status" } }
                            ]
                          },
                          "content": {
                            "__typename": "DraftIssue",
                            "id": "draft-node-backlog",
                            "title": "Backlog draft",
                            "body": "Not ready yet"
                          }
                        }
                      ]
                    }
                  }
                }
              }
            }
            """;

        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("items")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(JsonDocument.Parse(backlogDraftJson).RootElement);

        var svc = CreateClient(graphQL, rest);

        var result = await svc.TryGetNextItemInStatusAsync("Ready", CancellationToken.None);

        result.Should().BeNull("the only item is a Backlog-tagged draft, not Ready");
    }

    // ── §D.7: GetInFlightItemsAsync ───────────────────────────────────────────

    [Fact]
    public async Task GetInFlightItemsAsync_returns_items_from_both_InProgress_and_InReview()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("options")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(BuildOptionIdsResponse());

        // First call is InProgress query; second call is InReview query.
        // Items must carry the matching option ID or they will be filtered out.
        var call = 0;
        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("items")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(_ =>
               {
                   var c = call++;
                   return c == 0
                       ? BuildItemsResponse(includeIssue: true, includeDraftIssue: false, issueOptionId: "opt-inprogress")
                       : BuildItemsResponse(includeIssue: true, includeDraftIssue: false, issueOptionId: "opt-inreview");
               });

        var svc = CreateClient(graphQL, rest);

        var items = await svc.GetItemsInStatusesAsync(new[] { "In Progress", "In Review" }, CancellationToken.None);

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

        var svc = CreateClient(graphQL, rest);

        await svc.AddItemCommentAsync("issue-node-xyz", "Implementation plan: step 1", CancellationToken.None);

        await graphQL.Received(1).RunMutationAsync(
            Arg.Is<string>(s => s.Contains("addComment")),
            Arg.Is<Dictionary<string, object>?>(d =>
                d != null &&
                d["subjectId"].Equals("issue-node-xyz") &&
                d["body"].Equals("Implementation plan: step 1")),
            Arg.Any<CancellationToken>());
    }

    // ── §D.9: State filtering regression tests ────────────────────────────────

    [Fact]
    public async Task TryGetNextReadyItemAsync_returns_null_when_only_Done_items_exist()
    {
        // Items tagged opt-done must not surface from TryGetNextReadyItemAsync
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("options")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(BuildOptionIdsResponse());

        // Items query returns one item tagged with Done option
        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("items")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(BuildItemsResponse(issueOptionId: "opt-done"));

        var svc = CreateClient(graphQL, rest);

        var result = await svc.TryGetNextItemInStatusAsync("Ready", CancellationToken.None);

        result.Should().BeNull("a Done-tagged item must not be returned as Ready");
    }

    [Fact]
    public async Task TryGetNextReadyItemAsync_skips_non_Ready_items_returns_only_Ready()
    {
        // Items query returns a mix: one InProgress item, one Ready item
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("options")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(BuildOptionIdsResponse());

        // Build response with two items: InProgress (#10) and Ready (#42)
        var twoItemsJson = """
            {
              "data": {
                "organization": {
                  "projectV2": {
                    "items": {
                      "nodes": [
                        {
                          "id": "pvitem-10",
                          "fieldValues": {
                            "nodes": [
                              { "optionId": "opt-inprogress", "field": { "name": "Status" } }
                            ]
                          },
                          "content": {
                            "__typename": "Issue",
                            "id": "issue-node-10",
                            "number": 10,
                            "title": "Already started",
                            "body": ""
                          }
                        },
                        {
                          "id": "pvitem-42",
                          "fieldValues": {
                            "nodes": [
                              { "optionId": "opt-ready", "field": { "name": "Status" } }
                            ]
                          },
                          "content": {
                            "__typename": "Issue",
                            "id": "issue-node-42",
                            "number": 42,
                            "title": "Next ready task",
                            "body": ""
                          }
                        }
                      ]
                    }
                  }
                }
              }
            }
            """;

        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("items")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(JsonDocument.Parse(twoItemsJson).RootElement);

        var svc = CreateClient(graphQL, rest);

        var result = await svc.TryGetNextItemInStatusAsync("Ready", CancellationToken.None);

        result.Should().NotBeNull();
        result!.ContentNumber.Should().Be(42, "the InProgress item #10 must be skipped");
        result.Status.Should().Be("Ready");
    }

    [Fact]
    public async Task GetInFlightItemsAsync_returns_items_with_correct_State_values()
    {
        // First call (InProgress) returns item with opt-inprogress;
        // second call (InReview) returns item with opt-inreview.
        // After the fix, State fields must reflect the parsed option ID.
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("options")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(BuildOptionIdsResponse());

        var call = 0;
        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("items")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(_ =>
               {
                   var c = call++;
                   return c == 0
                       ? BuildItemsResponse(issueOptionId: "opt-inprogress", issueNumber: 1, issueTitle: "WIP task")
                       : BuildItemsResponse(issueOptionId: "opt-inreview", issueNumber: 2, issueTitle: "Under review");
               });

        var svc = CreateClient(graphQL, rest);

        var items = await svc.GetItemsInStatusesAsync(new[] { "In Progress", "In Review" }, CancellationToken.None);

        items.Should().HaveCount(2);
        items.Should().ContainSingle(i => i.Status == "In Progress", "first call returns InProgress item");
        items.Should().ContainSingle(i => i.Status == "In Review", "second call returns InReview item");
    }

    // ── §D.10: Unconfigured-GitHub guard ──────────────────────────────────────

    [Fact]
    public async Task TryGetNextReadyItemAsync_throws_GitHubNotConfiguredException_when_Owner_is_empty()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        var unconfigured = DefaultOptions() with { Owner = "" };

        var svc = CreateClient(graphQL, rest, unconfigured);

        var act = async () => await svc.TryGetNextItemInStatusAsync("Ready", CancellationToken.None);

        await act.Should().ThrowAsync<GitHubNotConfiguredException>()
            .WithMessage("*Owner*");

        // The guard must short-circuit before any GraphQL round-trip.
        await graphQL.DidNotReceive().RunQueryAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryGetNextReadyItemAsync_throws_GitHubNotConfiguredException_when_ProjectNumber_is_zero()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        var unconfigured = DefaultOptions() with
        {
            Project = new ProjectOptions { Number = 0, OwnerType = "Organization" }
        };

        var svc = CreateClient(graphQL, rest, unconfigured);

        var act = async () => await svc.TryGetNextItemInStatusAsync("Ready", CancellationToken.None);

        await act.Should().ThrowAsync<GitHubNotConfiguredException>()
            .WithMessage("*Project*Number*");

        await graphQL.DidNotReceive().RunQueryAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInFlightItemsAsync_throws_GitHubNotConfiguredException_when_Owner_is_empty()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        var unconfigured = DefaultOptions() with { Owner = "" };

        var svc = CreateClient(graphQL, rest, unconfigured);

        var act = async () => await svc.GetItemsInStatusesAsync(new[] { "In Progress", "In Review" }, CancellationToken.None);

        await act.Should().ThrowAsync<GitHubNotConfiguredException>();
    }

    // ── §D.11: GetReadyItemCountAsync ─────────────────────────────────────────

    [Fact]
    public async Task GetReadyItemCountAsync_returns_count_of_ready_items()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("options")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(BuildOptionIdsResponse());

        var twoReadyJson = """
            {
              "data": {
                "organization": {
                  "projectV2": {
                    "items": {
                      "nodes": [
                        {
                          "id": "pvitem-1",
                          "fieldValues": { "nodes": [ { "optionId": "opt-ready", "field": { "name": "Status" } } ] },
                          "content": { "__typename": "Issue", "id": "n1", "number": 1, "title": "Task A", "body": "" }
                        },
                        {
                          "id": "pvitem-2",
                          "fieldValues": { "nodes": [ { "optionId": "opt-ready", "field": { "name": "Status" } } ] },
                          "content": { "__typename": "Issue", "id": "n2", "number": 2, "title": "Task B", "body": "" }
                        }
                      ]
                    }
                  }
                }
              }
            }
            """;
        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("items")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(JsonDocument.Parse(twoReadyJson).RootElement);

        var svc = CreateClient(graphQL, rest);

        var count = await svc.GetItemCountInStatusAsync("Ready", CancellationToken.None);

        count.Should().Be(2);
    }

    // ── §D.12: RepositoryExistsAsync ─────────────────────────────────────────

    [Fact]
    public async Task RepositoryExistsAsync_returns_true_when_repository_found()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        rest.RepositoryExistsAsync("test-org", "test-repo", Arg.Any<CancellationToken>())
            .Returns(true);

        var svc = CreateClient(graphQL, rest);

        var result = await svc.RepositoryExistsAsync(CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task RepositoryExistsAsync_returns_false_when_repository_not_found()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        rest.RepositoryExistsAsync("test-org", "test-repo", Arg.Any<CancellationToken>())
            .Returns(false);

        var svc = CreateClient(graphQL, rest);

        var result = await svc.RepositoryExistsAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    // ── §D.13: ProjectExistsAsync ─────────────────────────────────────────────

    [Fact]
    public async Task ProjectExistsAsync_returns_true_when_project_found_in_graphql_response()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        graphQL.RunQueryAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(BuildProjectNodeIdResponse());

        var svc = CreateClient(graphQL, rest);

        var result = await svc.ProjectExistsAsync(CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task ProjectExistsAsync_returns_false_when_project_is_null_in_graphql_response()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        var json = """{"data":{"organization":{"projectV2":null}}}""";
        graphQL.RunQueryAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(JsonDocument.Parse(json).RootElement);

        var svc = CreateClient(graphQL, rest);

        var result = await svc.ProjectExistsAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    // ── §D.12: TryGetNextReadyItemAsync poll logging ──────────────────────────

    [Fact]
    public async Task TryGetNextReadyItemAsync_logs_project_owner_number_and_item_count()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();
        var logger = Substitute.For<ILogger<GitHubProjectsClient>>();

        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("options")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(BuildOptionIdsResponse());

        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("items")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(BuildItemsResponse(includeIssue: true));

        var svc = CreateClient(graphQL, rest, logger: logger);

        await svc.TryGetNextItemInStatusAsync("Ready", CancellationToken.None);

        logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("test-org") &&
                                o.ToString()!.Contains("1") &&
                                o.ToString()!.Contains("1")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task TryGetNextReadyItemAsync_logs_zero_count_when_no_ready_items()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();
        var logger = Substitute.For<ILogger<GitHubProjectsClient>>();

        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("options")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(BuildOptionIdsResponse());

        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("items")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(JsonDocument.Parse("""{"data":{"organization":{"projectV2":{"items":{"nodes":[]}}}}}""").RootElement);

        var svc = CreateClient(graphQL, rest, logger: logger);

        await svc.TryGetNextItemInStatusAsync("Ready", CancellationToken.None);

        logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("test-org") &&
                                o.ToString()!.Contains("0")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task TryGetNextReadyItemAsync_item_with_unknown_optionId_is_excluded()
    {
        // An item tagged with an unrecognized option (e.g., "Backlog") must be silently dropped
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("options")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(BuildOptionIdsResponse());

        // Item tagged with opt-backlog (not in the four known states)
        graphQL.RunQueryAsync(Arg.Is<string>(s => s.Contains("items")), Arg.Any<Dictionary<string, object>?>(), Arg.Any<CancellationToken>())
               .Returns(BuildItemsResponse(issueOptionId: "opt-backlog"));

        var svc = CreateClient(graphQL, rest);

        var result = await svc.TryGetNextItemInStatusAsync("Ready", CancellationToken.None);

        result.Should().BeNull("items with unrecognized option IDs must be excluded, not surfaced as Ready");
    }

    // ── §Step-28: GetPullRequestForReviewAsync / SubmitReviewAsync ────────────

    [Fact]
    public async Task GetPullRequestForReviewAsync_aggregates_body_files_and_diff()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        rest.GetPullRequestBodyAsync("test-org", "test-repo", 42, Arg.Any<CancellationToken>())
            .Returns("## Summary\nbody text");
        rest.GetPullRequestFilesAsync("test-org", "test-repo", 42, Arg.Any<CancellationToken>())
            .Returns(new List<RestPullRequestFile>
            {
                new("src/A.cs", Additions: 10, Deletions: 2, Patch: "@@ -1 +1 @@\n+a"),
                new("src/B.cs", Additions: 5,  Deletions: 3, Patch: "@@ -1 +1 @@\n+b"),
            });

        var svc = CreateClient(graphQL, rest);

        var ctx = await svc.GetPullRequestForReviewAsync(42, CancellationToken.None);

        ctx.Number.Should().Be(42);
        ctx.Body.Should().Be("## Summary\nbody text");
        ctx.ChangedFiles.Should().Be(2);
        ctx.ChangedLines.Should().Be(20); // (10+2) + (5+3)
        ctx.UnifiedDiff.Should().Contain("diff --git a/src/A.cs b/src/A.cs");
        ctx.UnifiedDiff.Should().Contain("diff --git a/src/B.cs b/src/B.cs");
        ctx.UnifiedDiff.Should().Contain("+a");
    }

    [Fact]
    public async Task GetPullRequestForReviewAsync_handles_file_without_patch()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();

        rest.GetPullRequestBodyAsync("test-org", "test-repo", 1, Arg.Any<CancellationToken>())
            .Returns(string.Empty);
        rest.GetPullRequestFilesAsync("test-org", "test-repo", 1, Arg.Any<CancellationToken>())
            .Returns(new List<RestPullRequestFile> { new("logo.png", 0, 0, Patch: null) });

        var svc = CreateClient(graphQL, rest);

        var ctx = await svc.GetPullRequestForReviewAsync(1, CancellationToken.None);

        ctx.ChangedFiles.Should().Be(1);
        ctx.ChangedLines.Should().Be(0);
        ctx.UnifiedDiff.Should().Contain("(no textual diff)");
    }

    [Fact]
    public async Task SubmitReviewAsync_forwards_verdict_and_body_to_transport()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();
        rest.SubmitReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<ReviewVerdict>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var svc = CreateClient(graphQL, rest);

        await svc.SubmitReviewAsync(99, ReviewVerdict.Approve, "LGTM", CancellationToken.None);

        await rest.Received(1).SubmitReviewAsync(
            "test-org", "test-repo", 99, ReviewVerdict.Approve, "LGTM", Arg.Any<CancellationToken>());
    }

    // ── §Step-55: PR mergeability ─────────────────────────────────────────────

    [Fact]
    public async Task GetPullRequestStatusAsync_surfaces_Mergeable_from_the_pull_request()
    {
        var graphQL = Substitute.For<IGraphQLTransport>();
        var rest = Substitute.For<IRestTransport>();
        rest.GetPullRequestAsync("test-org", "test-repo", 7, Arg.Any<CancellationToken>())
            .Returns(new RestPullRequest(7, "sha7", "https://gh/pr/7", Merged: false, Mergeable: true));
        rest.GetPullRequestReviewsAsync("test-org", "test-repo", 7, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RestPullRequestReview>());
        rest.GetCheckRunsAsync("test-org", "test-repo", "sha7", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RestCheckRun>());

        var client = CreateClient(graphQL, rest);

        var status = await client.GetPullRequestStatusAsync(7, CancellationToken.None);

        status.Mergeable.Should().BeTrue();
    }
}
