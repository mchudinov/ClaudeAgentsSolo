using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agent.Tools.Tests;

/// <summary>Unit tests for <see cref="ListDirectoryTool"/>.</summary>
public sealed class ListDirectoryToolTests : IDisposable
{
    private readonly string _root;
    private readonly IToolContext _ctx;
    private readonly ListDirectoryTool _tool = new();

    public ListDirectoryToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "list-dir-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _ctx = new TestToolContext(_root);

        // Create structure: root/a.txt, root/sub/b.cs
        File.WriteAllText(Path.Combine(_root, "a.txt"), "");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "b.cs"), "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Non_recursive_lists_top_level_only()
    {
        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\".\",\"recursive\":false}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var items = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(result.Content)!;
        items.Should().HaveCount(2); // a.txt + sub/
    }

    [Fact]
    public async Task Recursive_lists_all_entries()
    {
        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\".\",\"recursive\":true}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var items = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(result.Content)!;
        items.Should().HaveCountGreaterThan(2);
        items.Select(i => i["path"]).Should().Contain(p => p.Contains("b.cs"));
    }

    [Fact]
    public async Task Glob_filter_returns_matching_files_only()
    {
        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\".\",\"recursive\":true,\"glob\":\"*.cs\"}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeFalse();
        var items = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(result.Content)!;
        items.Should().AllSatisfy(i => i["path"].Should().EndWith(".cs"));
    }

    [Fact]
    public async Task Returns_error_for_missing_directory()
    {
        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\"nonexistent\"}")!, _ctx, CancellationToken.None);

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
}
