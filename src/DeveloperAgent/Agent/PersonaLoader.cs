using DeveloperAgent.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Agent;

/// <summary>
/// Loads and caches the developer persona from <see cref="AgentOptions.PersonaPath"/> at construction time.
/// Registered as a singleton in DI so the file is read exactly once at startup.
/// </summary>
public sealed class PersonaLoader
{
    /// <summary>
    /// The cached persona text.
    /// </summary>
    public string Persona { get; }

    /// <summary>
    /// Initialises the persona loader. Reads the persona file immediately.
    /// </summary>
    /// <param name="options">Agent options containing the persona path.</param>
    /// <param name="env">Host environment providing <c>ContentRootPath</c>.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the persona file is missing or empty — the agent cannot operate without a persona.
    /// </exception>
    public PersonaLoader(IOptions<AgentOptions> options, IHostEnvironment env)
    {
        var personaPath = options.Value.PersonaPath;

        // Resolve relative to ContentRootPath if not already absolute
        if (!Path.IsPathRooted(personaPath))
            personaPath = Path.Combine(env.ContentRootPath, personaPath);

        if (!File.Exists(personaPath))
            throw new InvalidOperationException(
                $"Persona file not found: {personaPath}. " +
                "Ensure the file is present at startup (check the csproj <Content Include> for personas/).");

        var text = File.ReadAllText(personaPath);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException(
                $"Persona file is empty: {personaPath}. " +
                "The agent requires a non-empty persona to operate.");

        Persona = text;
    }
}
