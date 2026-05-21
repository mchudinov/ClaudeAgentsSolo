using System.Text.Json.Nodes;
using DeveloperAgent.Agent;
using DeveloperAgent.Agent.Tools;
using DeveloperAgent.GitHub;
using DeveloperAgent.Workspace;

namespace DeveloperAgent.Tests.Agent.Tools;

/// <summary>Unit tests for <see cref="CommentOnItemTool"/>.</summary>
public sealed class CommentOnItemToolTests
{
    private readonly IGitHubProjectService _github = Substitute.For<IGitHubProjectService>();
    private readonly CommentOnItemTool _tool;
    private readonly ToolContext _ctx;

    public CommentOnItemToolTests()
    {
        _tool = new CommentOnItemTool(_github);
        var ws = new TaskWorkspace("item-1", "branch-1", Path.GetTempPath(), "main");
        _ctx = new ToolContext(new AgentSession(), ws,
            new ProjectItem("proj-item-id", "content-node-id", 42, "Title", "Body", ProjectState.InProgress));
    }

    [Fact]
    public async Task Posts_comment_and_returns_success()
    {
        _github.AddItemCommentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(Task.CompletedTask);

        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{\"body\":\"## Plan\\nStep 1\"}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("true");

        await _github.Received(1).AddItemCommentAsync(
            "content-node-id",
            "## Plan\nStep 1",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_error_for_missing_body()
    {
        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Returns_error_when_github_throws()
    {
        _github.AddItemCommentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns<Task>(_ => throw new InvalidOperationException("network error"));

        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{\"body\":\"test\"}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("network error");
    }
}
