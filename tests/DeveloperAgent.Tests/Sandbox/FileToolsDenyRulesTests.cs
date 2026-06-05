using System.Text.Json.Nodes;
using DeveloperAgent.Agent;
using DeveloperAgent.Agent.Tools;
using DeveloperAgent.GitHub;
using Agent.Workspace;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Tests.Sandbox;

/// <summary>
/// Integration of <see cref="IPathDenyPolicy"/> with <see cref="ReadFileTool"/>,
/// <see cref="WriteFileTool"/>, and <see cref="EditFileTool"/>.
/// </summary>
public sealed class FileToolsDenyRulesTests : IDisposable
{
    private readonly string _root;
    private readonly ToolContext _ctx;

    public FileToolsDenyRulesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "deny-tools-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var ws = new TaskWorkspace("item-1", "branch-1", _root, "main");
        _ctx = new ToolContext(new AgentRunState(), ws,
            new ProjectItem("pid", "cid", 1, "T", "B", ProjectState.InProgress));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private static IPathDenyPolicy DefaultPolicy() =>
        new PathDenyPolicy(Options.Create(ProductionSandboxConfig.Sandbox));

    private static IPathDenyPolicy PolicyWithRegex(string regex) =>
        new PathDenyPolicy(Options.Create(new SandboxOptions
        {
            DenyPathPatterns = ProductionSandboxConfig.DenyPathPatterns,
            SecretFileRegexes = [regex],
        }));

    // ── ReadFileTool ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadFile_returns_Denied_for_dotenv_in_workspace()
    {
        File.WriteAllText(Path.Combine(_root, ".env"), "SECRET=1");
        var tool = new ReadFileTool(DefaultPolicy());

        var result = await tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\".env\"}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.ErrorKind.Should().Be(ToolErrorKind.Denied);
    }

    [Fact]
    public async Task ReadFile_returns_Denied_for_git_config_in_workspace()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "config"), "[core]");
        var tool = new ReadFileTool(DefaultPolicy());

        var result = await tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\".git/config\"}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.ErrorKind.Should().Be(ToolErrorKind.Denied);
    }

    [Fact]
    public async Task ReadFile_returns_Denied_for_workspace_escape()
    {
        var tool = new ReadFileTool(DefaultPolicy());

        var result = await tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\"../../etc/passwd\"}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.ErrorKind.Should().Be(ToolErrorKind.Denied);
    }

    [Fact]
    public async Task ReadFile_allows_normal_workspace_file()
    {
        File.WriteAllText(Path.Combine(_root, "Program.cs"), "// hi");
        var tool = new ReadFileTool(DefaultPolicy());

        var result = await tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\"Program.cs\"}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("hi");
    }

    [Fact]
    public async Task ReadFile_returns_Denied_for_custom_secret_regex()
    {
        File.WriteAllText(Path.Combine(_root, "secret-keys.txt"), "k=v");
        var tool = new ReadFileTool(PolicyWithRegex(@"secret-keys\.txt$"));

        var result = await tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\"secret-keys.txt\"}")!, _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.ErrorKind.Should().Be(ToolErrorKind.Denied);
    }

    // ── WriteFileTool ─────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteFile_returns_Denied_for_dotenv()
    {
        var tool = new WriteFileTool(DefaultPolicy());

        var result = await tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\".env.production\",\"content\":\"x\"}")!,
            _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.ErrorKind.Should().Be(ToolErrorKind.Denied);
        File.Exists(Path.Combine(_root, ".env.production")).Should().BeFalse(
            because: "denied write must not touch the filesystem");
    }

    [Fact]
    public async Task WriteFile_returns_Denied_for_git_config()
    {
        var tool = new WriteFileTool(DefaultPolicy());

        var result = await tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\".git/config\",\"content\":\"[evil]\"}")!,
            _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.ErrorKind.Should().Be(ToolErrorKind.Denied);
    }

    [Fact]
    public async Task WriteFile_returns_Denied_for_workspace_escape()
    {
        var tool = new WriteFileTool(DefaultPolicy());

        var result = await tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\"../../evil.txt\",\"content\":\"x\"}")!,
            _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.ErrorKind.Should().Be(ToolErrorKind.Denied);
    }

    [Fact]
    public async Task WriteFile_allows_normal_workspace_file()
    {
        var tool = new WriteFileTool(DefaultPolicy());

        var result = await tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\"src/Foo.cs\",\"content\":\"// x\"}")!,
            _ctx, CancellationToken.None);

        result.IsError.Should().BeFalse();
        File.Exists(Path.Combine(_root, "src", "Foo.cs")).Should().BeTrue();
    }

    // ── EditFileTool ──────────────────────────────────────────────────────────

    [Fact]
    public async Task EditFile_returns_Denied_for_dotenv()
    {
        File.WriteAllText(Path.Combine(_root, ".env"), "OLD");
        var tool = new EditFileTool(DefaultPolicy());

        var result = await tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\".env\",\"old_string\":\"OLD\",\"new_string\":\"NEW\"}")!,
            _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.ErrorKind.Should().Be(ToolErrorKind.Denied);
        // File must be unchanged
        File.ReadAllText(Path.Combine(_root, ".env")).Should().Be("OLD");
    }

    [Fact]
    public async Task EditFile_returns_Denied_for_workspace_escape()
    {
        var tool = new EditFileTool(DefaultPolicy());

        var result = await tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\"../../etc/passwd\",\"old_string\":\"a\",\"new_string\":\"b\"}")!,
            _ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.ErrorKind.Should().Be(ToolErrorKind.Denied);
    }

    [Fact]
    public async Task EditFile_allows_normal_workspace_file()
    {
        File.WriteAllText(Path.Combine(_root, "f.cs"), "hello");
        var tool = new EditFileTool(DefaultPolicy());

        var result = await tool.InvokeAsync(
            JsonNode.Parse("{\"path\":\"f.cs\",\"old_string\":\"hello\",\"new_string\":\"bye\"}")!,
            _ctx, CancellationToken.None);

        result.IsError.Should().BeFalse();
        File.ReadAllText(Path.Combine(_root, "f.cs")).Should().Be("bye");
    }
}
