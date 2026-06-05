using Microsoft.Extensions.Hosting;

namespace Agent.Runtime;

/// <summary>
/// Loads and caches a markdown persona (system prompt) from a configured path at construction time.
/// Registered as a singleton so the file is read exactly once at startup; fails fast if the file is
/// missing or empty — an agent cannot operate without a persona.
/// <para>
/// Agent-neutral: it takes the persona path as a plain string. A host binds that string from its own
/// options type via a thin DI factory.
/// </para>
/// </summary>
public sealed class PersonaLoader
{
    /// <summary>The cached persona text.</summary>
    public string Persona { get; }

    /// <summary>
    /// Loads and caches the persona at <paramref name="personaPath"/> (relative to
    /// <c>ContentRootPath</c> or rooted).
    /// </summary>
    /// <param name="personaPath">The configured persona file path (rooted, or relative to the content root).</param>
    /// <param name="env">Host environment providing <c>ContentRootPath</c>.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the persona file is missing or empty.
    /// </exception>
    public PersonaLoader(string personaPath, IHostEnvironment env)
    {
        var configured = personaPath;
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
