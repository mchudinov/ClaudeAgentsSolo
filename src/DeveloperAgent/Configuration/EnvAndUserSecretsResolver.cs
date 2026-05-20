using Microsoft.Extensions.Configuration;

namespace DeveloperAgent.Configuration;

/// <summary>
/// Resolves secrets using the following precedence (highest to lowest):
/// <list type="number">
///   <item>User Secrets / IConfiguration — keyed by the secret name verbatim.</item>
///   <item>Environment variable — the secret name uppercased with hyphens replaced by underscores.</item>
///   <item>Throws <see cref="InvalidOperationException"/> with a clear message.</item>
/// </list>
/// </summary>
public sealed class EnvAndUserSecretsResolver(IConfiguration configuration) : ISecretResolver
{
    /// <inheritdoc />
    public string Resolve(string secretName)
    {
        // 1. User Secrets (or any IConfiguration provider — in Development, User Secrets
        //    are automatically loaded by WebApplication.CreateBuilder into the configuration root).
        var fromConfig = configuration[secretName];
        if (!string.IsNullOrEmpty(fromConfig))
            return fromConfig;

        // 2. Environment variable: uppercase + hyphens → underscores
        var envVarName = ToEnvVarName(secretName);
        var fromEnv = Environment.GetEnvironmentVariable(envVarName);
        if (!string.IsNullOrEmpty(fromEnv))
            return fromEnv;

        throw new InvalidOperationException(
            $"Secret '{secretName}' could not be resolved. " +
            $"Configure it via user-secrets (key: '{secretName}') " +
            $"or environment variable ('{envVarName}').");
    }

    private static string ToEnvVarName(string secretName) =>
        secretName.ToUpperInvariant().Replace('-', '_');
}
