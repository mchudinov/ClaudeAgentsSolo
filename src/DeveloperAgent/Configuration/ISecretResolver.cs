namespace DeveloperAgent.Configuration;

/// <summary>
/// Resolves named secrets from the runtime secret store.
/// Resolution order: User Secrets (Development) → environment variable → throw.
/// Phase 2 will add a Dapr Secrets API implementation behind the same interface.
/// </summary>
public interface ISecretResolver
{
    /// <summary>
    /// Returns the value of the named secret.
    /// </summary>
    /// <param name="secretName">
    /// The canonical secret name, e.g. <c>anthropic-api-key</c>.
    /// Hyphens are normalised to underscores and the name is uppercased when probing env vars.
    /// </param>
    /// <returns>The resolved secret value.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the secret cannot be found in any of the configured stores.
    /// </exception>
    string Resolve(string secretName);
}
