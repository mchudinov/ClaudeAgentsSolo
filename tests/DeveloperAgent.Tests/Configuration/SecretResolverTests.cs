using Library.Secrets;
using Microsoft.Extensions.Configuration;

namespace DeveloperAgent.Tests.Configuration;

public sealed class EnvAndUserSecretsResolverTests : IDisposable
{
    private readonly List<string> _envVarsSet = [];

    private void SetEnv(string key, string value)
    {
        Environment.SetEnvironmentVariable(key, value);
        _envVarsSet.Add(key);
    }

    public void Dispose()
    {
        foreach (var key in _envVarsSet)
            Environment.SetEnvironmentVariable(key, null);
    }

    [Fact]
    public void Resolve_returns_value_from_environment_variable()
    {
        SetEnv("ANTHROPIC_API_KEY", "test-api-key-value");

        var config = new ConfigurationBuilder().Build();
        var resolver = new EnvAndUserSecretsResolver(config);

        var result = resolver.Resolve("anthropic-api-key");

        result.Should().Be("test-api-key-value");
    }

    [Fact]
    public void Resolve_uppercases_and_underscores_secret_name()
    {
        SetEnv("MY_CUSTOM_SECRET", "secret-value");

        var config = new ConfigurationBuilder().Build();
        var resolver = new EnvAndUserSecretsResolver(config);

        var result = resolver.Resolve("my-custom-secret");

        result.Should().Be("secret-value");
    }

    [Fact]
    public void Resolve_throws_when_missing()
    {
        // Ensure env var is not set
        Environment.SetEnvironmentVariable("MISSING_SECRET_KEY", null);

        var config = new ConfigurationBuilder().Build();
        var resolver = new EnvAndUserSecretsResolver(config);

        var act = () => resolver.Resolve("missing-secret-key");

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*missing-secret-key*");
    }

    [Fact]
    public void Resolve_prefers_user_secrets_over_env_var()
    {
        SetEnv("ANTHROPIC_API_KEY", "env-value");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // User Secrets are surfaced as plain config keys by IConfiguration
                ["anthropic-api-key"] = "user-secret-value",
            })
            .Build();
        var resolver = new EnvAndUserSecretsResolver(config);

        var result = resolver.Resolve("anthropic-api-key");

        result.Should().Be("user-secret-value");
    }
}
