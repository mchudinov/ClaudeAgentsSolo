namespace Agent.Tools.Tests;

/// <summary>Unit tests for the <see cref="PathValidator"/> helper.</summary>
public sealed class PathValidatorTests
{
    // Use a stable temp root so paths look reasonable on both Windows and Linux
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "pv-tests", "repo");

    // ── happy paths ──────────────────────────────────────────────────────────

    [Fact]
    public void Relative_path_under_root_resolves()
    {
        var result = PathValidator.ResolveOrThrow("src/Foo.cs", Root);
        result.Should().Be(Path.GetFullPath(Path.Combine(Root, "src/Foo.cs")));
    }

    [Fact]
    public void Path_that_equals_repo_root_resolves()
    {
        var result = PathValidator.ResolveOrThrow(".", Root);
        result.Should().Be(Path.GetFullPath(Root));
    }

    [Fact]
    public void Absolute_path_inside_repo_root_resolves()
    {
        var inside = Path.GetFullPath(Path.Combine(Root, "sub", "file.txt"));
        var result = PathValidator.ResolveOrThrow(inside, Root);
        result.Should().Be(inside);
    }

    // ── escape attempts ──────────────────────────────────────────────────────

    [Fact]
    public void DotDot_escape_throws_InvalidOperationException()
    {
        var act = () => PathValidator.ResolveOrThrow("../../etc/passwd", Root);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*escapes workspace*");
    }

    [Fact]
    public void Absolute_path_outside_root_throws_InvalidOperationException()
    {
        // Path is in an entirely different directory tree
        var outside = Path.GetTempPath();
        var act = () => PathValidator.ResolveOrThrow(outside, Root);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*escapes workspace*");
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Windows_system_path_throws_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var act = () => PathValidator.ResolveOrThrow(@"C:\Windows\System32\cmd.exe", Root);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*escapes workspace*");
    }

    [Fact]
    public void Root_sibling_directory_that_shares_prefix_throws()
    {
        // e.g. Root = /tmp/pv-tests/repo
        //      attempt = /tmp/pv-tests/repo-evil/secret
        var sibling = Root + "-evil" + Path.DirectorySeparatorChar + "secret";
        var act = () => PathValidator.ResolveOrThrow(sibling, Root);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*escapes workspace*");
    }
}
