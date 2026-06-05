using DeveloperAgent.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Agent;

/// <summary>
/// Host wiring for the developer persona. The agent-neutral <see cref="PersonaLoader"/> (Step-47,
/// in <c>Agent.Runtime</c>) takes a plain string path; this extension is the "thin DI wrapper" that
/// binds that string from the host's <see cref="AgentOptions.PersonaPath"/> — the convenience the
/// loader's old <c>IOptions&lt;AgentOptions&gt;</c> ctor used to provide, kept host-side because the
/// library cannot reference the host's options type. Extracted into an extension (rather than an
/// inline <c>Program.cs</c> lambda) so the resolution is testable at unit speed.
/// </summary>
public static class PersonaRegistration
{
    /// <summary>
    /// Registers the developer <see cref="PersonaLoader"/> as a singleton, reading the persona path
    /// from <see cref="AgentOptions.PersonaPath"/> and resolving it against the host
    /// <see cref="IHostEnvironment"/>. Construction reads the file eagerly and throws if it is
    /// missing or empty.
    /// </summary>
    public static IServiceCollection AddDeveloperPersona(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(sp => new PersonaLoader(
            sp.GetRequiredService<IOptions<AgentOptions>>().Value.PersonaPath,
            sp.GetRequiredService<IHostEnvironment>()));

        return services;
    }
}
