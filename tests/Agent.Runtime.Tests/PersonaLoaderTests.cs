using Agent.Runtime;
using Microsoft.Extensions.Hosting;

namespace Agent.Runtime.Tests;

/// <summary>Unit tests for <see cref="PersonaLoader"/> (the string-ctor markdown loader).</summary>
public sealed class PersonaLoaderTests : IDisposable
{
    private readonly string _tempRoot;

    public PersonaLoaderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "persona-loader-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private IHostEnvironment Env()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(_tempRoot);
        return env;
    }

    // ── happy path ───────────────────────────────────────────────────────────

    [Fact]
    public void Persona_returns_file_content_when_relative_file_exists_under_content_root()
    {
        var personasDir = Path.Combine(_tempRoot, "personas");
        Directory.CreateDirectory(personasDir);
        File.WriteAllText(Path.Combine(personasDir, "developer.md"), "You are a senior .NET developer.");

        var loader = new PersonaLoader("personas/developer.md", Env());

        loader.Persona.Should().Be("You are a senior .NET developer.");
    }

    [Fact]
    public void Absolute_persona_path_is_used_as_is()
    {
        var file = Path.Combine(_tempRoot, "my-persona.md");
        File.WriteAllText(file, "Absolute path persona.");

        var loader = new PersonaLoader(file, Env());

        loader.Persona.Should().Be("Absolute path persona.");
    }

    // ── failure cases ────────────────────────────────────────────────────────

    [Fact]
    public void Missing_file_throws_InvalidOperationException()
    {
        var act = () => new PersonaLoader("personas/missing.md", Env());

        act.Should().Throw<InvalidOperationException>().WithMessage("*not found*");
    }

    [Fact]
    public void Empty_file_throws_InvalidOperationException()
    {
        var personasDir = Path.Combine(_tempRoot, "personas");
        Directory.CreateDirectory(personasDir);
        File.WriteAllText(Path.Combine(personasDir, "developer.md"), "   ");

        var act = () => new PersonaLoader("personas/developer.md", Env());

        act.Should().Throw<InvalidOperationException>().WithMessage("*empty*");
    }
}
