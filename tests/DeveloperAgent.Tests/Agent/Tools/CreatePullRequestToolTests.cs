using System.Text.Json;
using System.Text.Json.Nodes;
using DeveloperAgent.Agent;
using DeveloperAgent.Agent.Tools;
using DeveloperAgent.GitHub;
using DeveloperAgent.Workspace;

namespace DeveloperAgent.Tests.Agent.Tools;

/// <summary>Unit tests for <see cref="CreatePullRequestTool"/>.</summary>
public sealed class CreatePullRequestToolTests
{
    private readonly IGitHubProjectService _github = Substitute.For<IGitHubProjectService>();
    private readonly CreatePullRequestTool _tool;
    private readonly AgentRunState _session;
    private readonly ToolContext _ctx;

    public CreatePullRequestToolTests()
    {
        _tool = new CreatePullRequestTool(_github);
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
}
