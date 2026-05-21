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
        var configured = options.Value.PersonaPath;
        string? resolved = null;

        if (Path.IsPathRooted(configured))
        {
            if (File.Exists(configured)) resolved = configured;
        }
        else
        {
            // ContentRootPath first (so tests substituting a temp root work), then
            // AppContext.BaseDirectory (the published output dir where
            // <Content Include="..\..\personas\**\*.md" CopyToOutputDirectory> lands the file
            // in local dev when ContentRoot=src/DeveloperAgent doesn't itself contain personas/).
            foreach (var root in new[] { env.ContentRootPath, AppContext.BaseDirectory })
            {
                var candidate = Path.Combine(root, configured);
                if (File.Exists(candidate)) { resolved = candidate; break; }
            }
        }

        if (resolved is null)
            throw new InvalidOperationException(
                $"Persona file not found for configured path '{configured}'. Tried " +
                $"{AppContext.BaseDirectory} and {env.ContentRootPath}. " +
                "Ensure the file is present at startup (check the csproj <Content Include> for personas/).");

        var text = File.ReadAllText(resolved);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException(
                $"Persona file is empty: {resolved}. The agent requires a non-empty persona.");

        Persona = text;
    }
}
