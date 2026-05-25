using System.Text.Json.Nodes;
using DeveloperAgent.Agent;
using DeveloperAgent.Agent.Tools;
using DeveloperAgent.GitHub;
using DeveloperAgent.Sandbox;
using DeveloperAgent.Workspace;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Tests.Agent.Tools;

/// <summary>Unit tests for <see cref="EditFileTool"/>.</summary>
public sealed class EditFileToolTests : IDisposable
{
    private static readonly IPathDenyPolicy NoOpDeny =
        new PathDenyPolicy(Options.Create(new SandboxOptions
        {
            DenyPathPatterns = [],
            SecretFileRegexes = [],
        }));

    private readonly string _root;
    private readonly ToolContext _ctx;
    private readonly EditFileTool _tool = new(NoOpDeny);

    public EditFileToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "edit-file-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var ws = new TaskWorkspace("item-1", "branch-1", _root, "main");
        _ctx = new ToolContext(new AgentSession(), ws,
            new ProjectItem("pid", "cid", 1, "T", "B", ProjectState.InProgress));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private void WriteFile(string name, string content) =>
        File.WriteAllText(Path.Combine(_root, name), content);

    [Fact]
    public async Task Replaces_single_occurrence_of_old_string()
    {
        WriteFile("f.txt", "hello world");

        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\"f.txt\",\"old_string\":\"world\",\"new_string\":\"there\"}")!,
            _ctx, CancellationToken.None);

        result.IsError.Should().BeFalse();
        File.ReadAllText(Path.Combine(_root, "f.txt")).Should().Be("hello there");
    }

    [Fact]
    public async Task Returns_error_when_old_string_absent()
    {
        WriteFile("f.txt", "hello world");

        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\"f.txt\",\"old_string\":\"missing\",\"new_string\":\"x\"}")!,
            _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("not found");
    }

    [Fact]
    public async Task Returns_error_when_old_string_appears_more_than_once()
    {
        WriteFile("f.txt", "foo foo");

        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\"f.txt\",\"old_string\":\"foo\",\"new_string\":\"bar\"}")!,
            _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("more than once");
    }

    [Fact]
    public async Task Returns_error_for_missing_file()
    {
        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\"no.txt\",\"old_string\":\"x\",\"new_string\":\"y\"}")!,
            _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("not found");
    }

    [Fact]
    public async Task Returns_error_for_invalid_input_missing_path()
    {
        var result = await _tool.InvokeAsync(
            JsonNode.Parse("{\"old_string\":\"x\",\"new_string\":\"y\"}")!,
            _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
    }
}
