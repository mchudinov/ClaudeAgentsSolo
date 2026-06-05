using System.Text.Json.Nodes;

namespace Agent.Tools.Tests;

/// <summary>Unit tests for <see cref="ReadFileTool"/>.</summary>
public sealed class ReadFileToolTests : IDisposable
{
    private static readonly IPathDenyPolicy NoOpDeny = new WorkspaceBoundaryDenyPolicy();

    private readonly string _root;
    private readonly IToolContext _ctx;
    private readonly ReadFileTool _tool = new(NoOpDeny);

    public ReadFileToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "read-file-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _ctx = new TestToolContext(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

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
