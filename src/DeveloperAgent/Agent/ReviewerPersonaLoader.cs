using DeveloperAgent.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Agent;

/// <summary>
/// Loads and caches the reviewer persona from <see cref="ReviewerOptions.PersonaPath"/> at
/// construction time. A distinct DI type from <see cref="PersonaLoader"/> (which loads the
/// developer persona) so both can be registered as singletons. Delegates file resolution to
/// <see cref="PersonaLoader"/> so the resolution logic lives in one place.
/// </summary>
public sealed class ReviewerPersonaLoader
{
    /// <summary>The cached reviewer persona text.</summary>
    public string Persona { get; }

    public ReviewerPersonaLoader(IOptions<ReviewerOptions> options, IHostEnvironment env)
    {
        Persona = new PersonaLoader(options.Value.PersonaPath, env).Persona;
    }
}
