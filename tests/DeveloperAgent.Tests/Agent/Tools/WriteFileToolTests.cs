using System.Text.Json.Nodes;
using DeveloperAgent.Agent;
using DeveloperAgent.Agent.Tools;
using DeveloperAgent.GitHub;
using DeveloperAgent.Workspace;

namespace DeveloperAgent.Tests.Agent.Tools;

/// <summary>Unit tests for <see cref="WriteFileTool"/>.</summary>
public sealed class WriteFileToolTests : IDisposable
{
    private readonly string _root;
    private readonly ToolContext _ctx;
    private readonly WriteFileTool _tool = new();

    public WriteFileToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "write-file-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var ws = new TaskWorkspace("item-1", "branch-1", _root, "main");
        _ctx = new ToolContext(new AgentSession(), ws,
            new ProjectItem("pid", "cid", 1, "T", "B", ProjectState.InProgress));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Creates_file_with_content()
    {
        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\"new.txt\",\"content\":\"data\"}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("new.txt");
        File.ReadAllText(Path.Combine(_root, "new.txt")).Should().Be("data");
    }

    [Fact]
    public async Task Creates_parent_directories_as_needed()
    {
        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\"a/b/c.txt\",\"content\":\"nested\"}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeFalse();
        File.Exists(Path.Combine(_root, "a", "b", "c.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Overwrites_existing_file()
    {
        File.WriteAllText(Path.Combine(_root, "existing.txt"), "old");

        await _tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\"existing.txt\",\"content\":\"new\"}")!, _ctx, CancellationToken.None);

        File.ReadAllText(Path.Combine(_root, "existing.txt")).Should().Be("new");
    }

    [Fact]
    public async Task Returns_error_for_missing_path()
    {
        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{\"content\":\"data\"}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Returns_error_for_path_escaping_workspace()
    {
        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\"../../evil.txt\",\"content\":\"data\"}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("escapes workspace");
    }
}
