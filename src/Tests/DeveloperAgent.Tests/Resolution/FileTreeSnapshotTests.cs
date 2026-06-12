using DeveloperAgent.Resolution;

namespace DeveloperAgent.Tests.Resolution;

/// <summary>
/// Unit tests for <see cref="FileTreeSnapshot"/> — the deterministic working-tree evidence the
/// already-resolved checker feeds to the model. Verifies it lists real source files, excludes build
/// output, and tolerates a missing path.
/// </summary>
public sealed class FileTreeSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fts-" + Guid.NewGuid().ToString("N"));

    private void Write(string relative, string content = "x")
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public async Task Captures_source_files_as_forward_slash_relative_paths()
    {
        Write("src/Services/WidgetService.cs");
        Write("README.md");

        var snapshot = await new FileTreeSnapshot().CaptureAsync(_root, CancellationToken.None);

        snapshot.Should().Contain("src/Services/WidgetService.cs");
        snapshot.Should().Contain("README.md");
    }

    [Fact]
    public async Task Excludes_build_output_and_git_directories()
    {
        Write("src/Foo.cs");
        Write("bin/Debug/net10.0/Foo.dll");
        Write("obj/project.assets.json");
        Write(".git/HEAD");

        var snapshot = await new FileTreeSnapshot().CaptureAsync(_root, CancellationToken.None);

        snapshot.Should().Contain("src/Foo.cs");
        snapshot.Should().NotContain("Foo.dll", because: "bin/ output is noise, not evidence of implementation");
        snapshot.Should().NotContain("project.assets.json", because: "obj/ output must be excluded");
        snapshot.Should().NotContain("HEAD", because: ".git internals must be excluded");
    }

    [Fact]
    public async Task Missing_or_blank_path_returns_empty_string()
    {
        (await new FileTreeSnapshot().CaptureAsync(Path.Combine(_root, "does-not-exist"), CancellationToken.None))
            .Should().BeEmpty();
        (await new FileTreeSnapshot().CaptureAsync("   ", CancellationToken.None))
            .Should().BeEmpty();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }
}
