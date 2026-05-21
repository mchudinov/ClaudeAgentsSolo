using DeveloperAgent.Agent.Tools;
using DeveloperAgent.Workspace;

namespace DeveloperAgent.Tests.Agent;

/// <summary>Unit tests for the internal <see cref="PathValidator"/> helper.</summary>
public sealed class PathValidatorTests
{
    // Use a stable temp root so paths look reasonable on both Windows and Linux
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "pv-tests", "repo");

    private static TaskWorkspace MakeWorkspace() =>
        new("item-1", "branch-1", Root, "main");

    // ── happy paths ──────────────────────────────────────────────────────────

    [Fact]
    public void Relative_path_under_root_resolves()
    {
        var ws = MakeWorkspace();
        var result = PathValidator.ResolveOrThrow("src/Foo.cs", ws);
        result.Should().Be(Path.GetFullPath(Path.Combine(Root, "src/Foo.cs")));
    }

    [Fact]
    public void Path_that_equals_repo_root_resolves()
    {
        var ws = MakeWorkspace();
        var result = PathValidator.ResolveOrThrow(".", ws);
        result.Should().Be(Path.GetFullPath(Root));
    }

    [Fact]
    public void Absolute_path_inside_repo_root_resolves()
    {
        var ws = MakeWorkspace();
        var inside = Path.GetFullPath(Path.Combine(Root, "sub", "file.txt"));
        var result = PathValidator.ResolveOrThrow(inside, ws);
        result.Should().Be(inside);
    }

    // ── escape attempts ──────────────────────────────────────────────────────

    [Fact]
    public void DotDot_escape_throws_InvalidOperationException()
    {
        var ws = MakeWorkspace();
        var act = () => PathValidator.ResolveOrThrow("../../etc/passwd", ws);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*escapes workspace*");
    }

    [Fact]
    public void Absolute_path_outside_root_throws_InvalidOperationException()
    {
        var ws = MakeWorkspace();
        // Path is in an entirely different directory tree
        var outside = Path.GetTempPath();
        var act = () => PathValidator.ResolveOrThrow(outside, ws);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*escapes workspace*");
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Windows_system_path_throws_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var ws = MakeWorkspace();
        var act = () => PathValidator.ResolveOrThrow(@"C:\Windows\System32\cmd.exe", ws);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*escapes workspace*");
    }

    [Fact]
    public void Root_sibling_directory_that_shares_prefix_throws()
    {
        // e.g. Root = /tmp/pv-tests/repo
        //      attempt = /tmp/pv-tests/repo-evil/secret
        var ws = MakeWorkspace();
        var sibling = Root + "-evil" + Path.DirectorySeparatorChar + "secret";
        var act = () => PathValidator.ResolveOrThrow(sibling, ws);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*escapes workspace*");
    }
}
