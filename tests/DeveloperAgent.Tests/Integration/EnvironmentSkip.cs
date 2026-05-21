namespace DeveloperAgent.Tests.Integration;

/// <summary>
/// Helper for conditionally skipping integration tests when required environment variables are absent.
/// </summary>
internal static class EnvironmentSkip
{
    /// <summary>
    /// Returns <see langword="null"/> when all specified environment variables are set and non-empty
    /// (meaning the test should run). Returns a human-readable skip reason when any variable is
    /// absent or empty.
    /// </summary>
    /// <param name="envVarNames">One or more environment variable names that must all be present.</param>
    public static string? ReasonIfMissing(params string[] envVarNames)
    {
        var missing = envVarNames
            .Where(v => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v)))
            .ToArray();

        if (missing.Length == 0)
            return null;

        return $"Skipped: requires env var(s): {string.Join(", ", missing)}";
    }
}
