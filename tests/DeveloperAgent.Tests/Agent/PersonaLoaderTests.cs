using DeveloperAgent.Agent;
using DeveloperAgent.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Tests.Agent;

/// <summary>Unit tests for <see cref="PersonaLoader"/>.</summary>
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

    private IOptions<AgentOptions> Options(string personaPath) =>
        Microsoft.Extensions.Options.Options.Create(new AgentOptions { PersonaPath = personaPath });

    private IHostEnvironment Env() =>
        CreateEnv(_tempRoot);

    private static IHostEnvironment CreateEnv(string contentRoot)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(contentRoot);
        return env;
    }

    // ── happy path ───────────────────────────────────────────────────────────

    [Fact]
    public void Persona_returns_file_content_when_file_exists()
    {
        // Arrange: create personas/developer.md in temp root
        var personasDir = Path.Combine(_tempRoot, "personas");
        Directory.CreateDirectory(personasDir);
        var personaFile = Path.Combine(personasDir, "developer.md");
        File.WriteAllText(personaFile, "You are a senior .NET developer.");

        var loader = new PersonaLoader(Options("personas/developer.md"), Env());

        loader.Persona.Should().Be("You are a senior .NET developer.");
    }

    [Fact]
    public void Absolute_persona_path_is_used_as_is()
    {
        // Arrange: create the file at an absolute path
        var file = Path.Combine(_tempRoot, "my-persona.md");
        File.WriteAllText(file, "Absolute path persona.");

        var loader = new PersonaLoader(Options(file), Env());

        loader.Persona.Should().Be("Absolute path persona.");
    }

    // ── failure cases ────────────────────────────────────────────────────────

    [Fact]
    public void Missing_file_throws_InvalidOperationException()
    {
        var act = () => new PersonaLoader(Options("personas/missing.md"), Env());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public void Empty_file_throws_InvalidOperationException()
    {
        var personasDir = Path.Combine(_tempRoot, "personas");
        Directory.CreateDirectory(personasDir);
        File.WriteAllText(Path.Combine(personasDir, "developer.md"), "   ");

        var act = () => new PersonaLoader(Options("personas/developer.md"), Env());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*empty*");
    }
}
