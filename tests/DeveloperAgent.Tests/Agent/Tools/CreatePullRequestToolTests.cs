using System.Text.Json;
using System.Text.Json.Nodes;
using DeveloperAgent.Agent;
using DeveloperAgent.Agent.Tools;
using DeveloperAgent.Configuration;
using DeveloperAgent.GitHub;
using DeveloperAgent.Workspace;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Tests.Agent.Tools;

/// <summary>Unit tests for <see cref="CreatePullRequestTool"/>.</summary>
public sealed class CreatePullRequestToolTests
{
    private readonly IGitHubProjectService _github = Substitute.For<IGitHubProjectService>();
    private readonly IGitClient _gitClient = Substitute.For<IGitClient>();
    private readonly CreatePullRequestTool _tool;
    private readonly AgentRunState _session;
    private readonly ToolContext _ctx;

    public CreatePullRequestToolTests()
        : this(new ScopeLimitOptions())
    {
    }

    private CreatePullRequestToolTests(ScopeLimitOptions scopeLimits)
    {
        // Default: diff stats well within limits so the gate never fires unless a test
        // overrides it via a dedicated scope-limit instance.
        _gitClient.GetDiffStatsAsync(Arg.Any<TaskWorkspace>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns(new DiffStats(1, 5));
        _tool = new CreatePullRequestTool(_github, _gitClient, Options.Create(scopeLimits));
        _session = new AgentRunState();
        var ws = new TaskWorkspace("item-1", "agent/fix-bug", Path.GetTempPath(), "main");
        _ctx = new ToolContext(_session, ws,
            new ProjectItem("proj-item-id", "content-node-id", 42, "Title", "Body", ProjectState.InProgress));
    }

    private static string ValidInput(string? title = null) =>
        title is null
            ? "{\"summary\":\"Fix the bug.\",\"user_visible_behavior\":\"None\",\"tests_validation_run\":\"dotnet test → 5 passed\",\"notes_assumptions\":\"None\"}"
            : $"{{\"summary\":\"Fix the bug.\",\"user_visible_behavior\":\"None\",\"tests_validation_run\":\"dotnet test → 5 passed\",\"notes_assumptions\":\"None\",\"title\":\"{title}\"}}";

    [Fact]
    public async Task Creates_pull_request_and_stores_on_session()
    {
        var pr = new PullRequest(99, "sha-abc", "https://github.com/foo/bar/pull/99");
        _github.CreatePullRequestAsync(Arg.Any<CreatePullRequest>(), Arg.Any<CancellationToken>())
               .Returns(pr);

        var result = await _tool.InvokeAsync(
            JsonNode.Parse(ValidInput())!, _ctx, CancellationToken.None);

        result.IsError.Should().BeFalse();
        _session.CreatedPullRequest.Should().Be(pr);

        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(result.Content)!;
        payload["number"].GetInt32().Should().Be(99);
        payload["html_url"].GetString().Should().Be("https://github.com/foo/bar/pull/99");
    }

    [Fact]
    public async Task Uses_title_override_when_provided()
    {
        CreatePullRequest? captured = null;
        _github.CreatePullRequestAsync(
            Arg.Do<CreatePullRequest>(r => captured = r),
            Arg.Any<CancellationToken>())
            .Returns(new PullRequest(1, "sha", "https://github.com/foo/pull/1"));

        await _tool.InvokeAsync(
            JsonNode.Parse(ValidInput("My custom title"))!, _ctx, CancellationToken.None);

        captured!.Title.Should().Be("My custom title");
    }

    [Fact]
    public async Task Derives_title_from_first_sentence_of_summary()
    {
        CreatePullRequest? captured = null;
        _github.CreatePullRequestAsync(
            Arg.Do<CreatePullRequest>(r => captured = r),
            Arg.Any<CancellationToken>())
            .Returns(new PullRequest(1, "sha", "https://github.com/foo/pull/1"));

        await _tool.InvokeAsync(
            JsonNode.Parse("{\"summary\":\"Fix the auth bug. Also adds logging.\"}")!,
            _ctx, CancellationToken.None);

        captured!.Title.Should().Be("Fix the auth bug.");
    }

    [Fact]
    public async Task Returns_error_for_missing_summary()
    {
        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Returns_error_for_empty_summary()
    {
        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{\"summary\":\"   \"}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Returns_error_when_github_throws()
    {
        _github.CreatePullRequestAsync(Arg.Any<CreatePullRequest>(), Arg.Any<CancellationToken>())
               .Returns<PullRequest>(_ => throw new InvalidOperationException("rate limited"));

        var result = await _tool.InvokeAsync(
            JsonNode.Parse(ValidInput())!, _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("rate limited");
    }

    [Fact]
    public async Task PR_body_contains_four_sections()
    {
        CreatePullRequest? captured = null;
        _github.CreatePullRequestAsync(
            Arg.Do<CreatePullRequest>(r => captured = r),
            Arg.Any<CancellationToken>())
            .Returns(new PullRequest(1, "sha", "https://github.com/foo/pull/1"));

        await _tool.InvokeAsync(
            JsonNode.Parse(ValidInput())!, _ctx, CancellationToken.None);

        captured!.MarkdownBody.Should().Contain("## Summary");
        captured.MarkdownBody.Should().Contain("## User-visible behavior");
        captured.MarkdownBody.Should().Contain("## Tests/validation run");
        captured.MarkdownBody.Should().Contain("## Notes/assumptions");
    }

    // ── MaxPRSize gate (Step-21, P2-H) ────────────────────────────────────────

    /// <summary>
    /// Builds a tool wired with a stub git client returning <paramref name="stats"/> and the
    /// given scope limits. Returns the tool plus the substitutes so tests can assert on them.
    /// </summary>
    private static (CreatePullRequestTool Tool, IGitHubProjectService Github, ToolContext Ctx)
        BuildWithStats(DiffStats stats, ScopeLimitOptions limits)
    {
        var github = Substitute.For<IGitHubProjectService>();
        var gitClient = Substitute.For<IGitClient>();
        gitClient.GetDiffStatsAsync(Arg.Any<TaskWorkspace>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(stats);
        var tool = new CreatePullRequestTool(github, gitClient, Options.Create(limits));
        var ctx = new ToolContext(
            new AgentRunState(),
            new TaskWorkspace("item-1", "agent/fix-bug", Path.GetTempPath(), "main"),
            new ProjectItem("proj-item-id", "content-node-id", 42, "Title", "Body", ProjectState.InProgress));
        return (tool, github, ctx);
    }

    [Fact]
    public async Task Blocks_PR_and_comments_when_changed_files_exceed_MaxPRSize()
    {
        var (tool, github, ctx) = BuildWithStats(
            new DiffStats(ChangedFiles: 60, ChangedLines: 100),
            new ScopeLimitOptions { MaxPRChangedFiles = 50, MaxPRChangedLines = 2_000 });

        var result = await tool.InvokeAsync(
            JsonNode.Parse(ValidInput())!, ctx, CancellationToken.None);

        // Halt: error returned, no PR opened, explanatory comment posted to the item.
        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("MaxPRSize");
        ctx.Session.CreatedPullRequest.Should().BeNull();
        await github.DidNotReceive().CreatePullRequestAsync(
            Arg.Any<CreatePullRequest>(), Arg.Any<CancellationToken>());
        await github.Received(1).AddItemCommentAsync(
            "content-node-id",
            Arg.Is<string>(s => s.Contains("MaxPRSize") && s.Contains("60")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Blocks_PR_and_comments_when_changed_lines_exceed_MaxPRSize()
    {
        var (tool, github, ctx) = BuildWithStats(
            new DiffStats(ChangedFiles: 3, ChangedLines: 5_000),
            new ScopeLimitOptions { MaxPRChangedFiles = 50, MaxPRChangedLines = 2_000 });

        var result = await tool.InvokeAsync(
            JsonNode.Parse(ValidInput())!, ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
        ctx.Session.CreatedPullRequest.Should().BeNull();
        await github.DidNotReceive().CreatePullRequestAsync(
            Arg.Any<CreatePullRequest>(), Arg.Any<CancellationToken>());
        await github.Received(1).AddItemCommentAsync(
            Arg.Any<string>(),
            Arg.Is<string>(s => s.Contains("5000")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Opens_PR_when_within_MaxPRSize()
    {
        var (tool, github, ctx) = BuildWithStats(
            new DiffStats(ChangedFiles: 5, ChangedLines: 200),
            new ScopeLimitOptions { MaxPRChangedFiles = 50, MaxPRChangedLines = 2_000 });
        github.CreatePullRequestAsync(Arg.Any<CreatePullRequest>(), Arg.Any<CancellationToken>())
              .Returns(new PullRequest(7, "sha", "https://github.com/foo/pull/7"));

        var result = await tool.InvokeAsync(
            JsonNode.Parse(ValidInput())!, ctx, CancellationToken.None);

        result.IsError.Should().BeFalse();
        ctx.Session.CreatedPullRequest.Should().NotBeNull();
        await github.DidNotReceive().AddItemCommentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
