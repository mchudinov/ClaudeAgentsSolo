using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Hosting;

namespace ReviewerAgent.Tests.Configuration;

/// <summary>
/// Guards the configuration-source wiring in <see cref="Program.ConfigureConfigSources"/>. The shared
/// User Secrets store (the agent's UserSecretsId) holds <c>anthropic-api-key</c> and <c>github-token</c>,
/// but <c>WebApplication.CreateBuilder</c> only registers the User Secrets provider in the Development
/// environment. ReviewerAgent must add it explicitly so those secrets resolve regardless of environment
/// (e.g. launched without <c>ASPNETCORE_ENVIRONMENT=Development</c>, or under the Aspire AppHost).
/// Dropping that wiring reintroduces the "Secret 'anthropic-api-key' could not be resolved" failure.
/// </summary>
public sealed class ConfigSourcesTests
{
    [Fact]
    public void User_secrets_source_is_added_even_outside_Development()
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);

        var builder = new ConfigurationBuilder();
        Program.ConfigureConfigSources(builder, environment);

        builder.Sources.OfType<JsonConfigurationSource>()
            .Select(source => source.Path)
            .Should().Contain(
                "secrets.json",
                "the shared User Secrets store must load outside Development so anthropic-api-key/github-token resolve");
    }
}
