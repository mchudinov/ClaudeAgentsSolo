namespace ClaudeAgents.GitHub;

/// <summary>GitHub identity and project configuration consumed by the agent-neutral GitHub layer.
/// Bound by the host from its <c>GitHub</c> configuration section. Lifecycle column-name policy
/// (e.g. the developer agent's <c>ProjectStateNames</c>) is deliberately NOT here — it is host policy.</summary>
public sealed record GitHubOptions
{
    /// <summary>GitHub organisation or user that owns the repository and project.</summary>
    public string Owner { get; init; } = "";

    /// <summary>Repository settings.</summary>
    public RepositoryOptions Repository { get; init; } = new();

    /// <summary>GitHub Project (v2) settings.</summary>
    public ProjectOptions Project { get; init; } = new();

    /// <summary>
    /// Name of the secret holding the GitHub PAT.
    /// In Development: user-secrets key. In production: env-var name (uppercased, hyphens → underscores).
    /// </summary>
    public string TokenSecretName { get; init; } = "github-token";
}

/// <summary>Repository identity.</summary>
public sealed record RepositoryOptions
{
    /// <summary>Short repository name (without owner prefix).</summary>
    public string Name { get; init; } = "";

    /// <summary>Full HTTPS clone URL of the repository.</summary>
    public string Url { get; init; } = "";

    /// <summary>Default branch name (used for PR base and push guard).</summary>
    public string DefaultBranch { get; init; } = "main";
}

/// <summary>GitHub Project (v2) identity.</summary>
public sealed record ProjectOptions
{
    /// <summary>Display name of the project.</summary>
    public string Name { get; init; } = "";

    /// <summary>Project number as shown in the GitHub UI URL.</summary>
    public int Number { get; init; }

    /// <summary>Whether the project belongs to an Organization or a User account.</summary>
    public string OwnerType { get; init; } = "Organization";
}
