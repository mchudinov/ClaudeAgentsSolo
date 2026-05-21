using System.Text.Json.Nodes;
using DeveloperAgent.Agent;
using DeveloperAgent.Agent.Tools;
using DeveloperAgent.GitHub;
using DeveloperAgent.Workspace;

namespace DeveloperAgent.Tests.Agent.Tools;

/// <summary>Unit tests for <see cref="ReadFileTool"/>.</summary>
public sealed class ReadFileToolTests : IDisposable
{
    private readonly string _root;
    private readonly TaskWorkspace _ws;
    private readonly ToolContext _ctx;
    private readonly ReadFileTool _tool = new();

    public ReadFileToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "read-file-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _ws = new TaskWorkspace("item-1", "branch-1", _root, "main");
        _ctx = MakeContext(_ws);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private static ToolContext MakeContext(TaskWorkspace ws) =>
        new(new AgentSession(), ws,
            new ProjectItem("pid", "cid", 1, "Title", "Body", ProjectState.InProgress));

    [Fact]
    public async Task Returns_file_content_for_existing_file()
    {
        File.WriteAllText(Path.Combine(_root, "hello.txt"), "hello world");

        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\"hello.txt\"}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Be("hello world");
    }

    [Fact]
    public async Task Returns_error_for_missing_file()
    {
        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\"nonexistent.txt\"}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("not found");
    }

    [Fact]
    public async Task Returns_error_for_missing_path_parameter()
    {
        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Returns_error_for_path_escaping_workspace()
    {
        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\"../../etc/passwd\"}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("escapes workspace");
    }
}
