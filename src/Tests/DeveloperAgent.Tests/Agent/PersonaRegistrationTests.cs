using DeveloperAgent.Agent;
using DeveloperAgent.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Tests.Agent;

/// <summary>
/// Guards the host wiring that binds <see cref="AgentOptions.PersonaPath"/> into the agent-neutral
/// <see cref="PersonaLoader"/> (Agent.Runtime). Step-47 removed the loader's
/// <c>IOptions&lt;AgentOptions&gt;</c> ctor (the library cannot see the host's options type) and
/// replaced it with the <see cref="PersonaRegistration.AddDeveloperPersona"/> factory. This test
/// keeps that "read PersonaPath from AgentOptions and resolve" behavior covered after the move —
/// mirroring <c>WorkflowRegistrationTests</c>, it reproduces the startup resolution at unit speed.
/// </summary>
public sealed class PersonaRegistrationTests : IDisposable
{
    private readonly string _tempRoot;

    public PersonaRegistrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "persona-registration-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void AddDeveloperPersona_resolves_PersonaLoader_from_configured_AgentOptions_PersonaPath()
    {
        // A deliberately NON-default path (AgentOptions.PersonaPath defaults to "personas/developer.md").
        // If AddDeveloperPersona ignored AgentOptions and hardcoded the default, the file below would
        // not be found and PersonaLoader's ctor would throw — so this proves the path is bound FROM
        // AgentOptions, not merely that some persona file resolves under ContentRootPath. Do not
        // "simplify" this back to the default filename.
        const string customPath = "personas/custom-role.md";
        var personasDir = Path.Combine(_tempRoot, "personas");
        Directory.CreateDirectory(personasDir);
        File.WriteAllText(Path.Combine(personasDir, "custom-role.md"), "You are a custom-role agent.");

        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(_tempRoot);

        var services = new ServiceCollection();
        services.AddSingleton(env);
        services.AddSingleton<IOptions<AgentOptions>>(
            Options.Create(new AgentOptions { PersonaPath = customPath }));

        services.AddDeveloperPersona();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<PersonaLoader>().Persona
            .Should().Be("You are a custom-role agent.", "the loader must read the path bound from AgentOptions");
    }
}
