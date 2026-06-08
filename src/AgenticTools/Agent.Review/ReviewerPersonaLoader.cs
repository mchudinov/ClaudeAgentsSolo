using Agent.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agent.Review;

/// <summary>
/// Loads and caches the reviewer persona from <see cref="ReviewerOptions.PersonaPath"/> at
/// construction. Delegates file resolution to the Agent.Runtime <see cref="PersonaLoader"/> so
/// the path-resolution logic lives in one place. A distinct DI type so it can be a singleton.
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
