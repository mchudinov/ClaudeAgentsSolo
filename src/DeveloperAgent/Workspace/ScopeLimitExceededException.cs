namespace DeveloperAgent.Workspace;

/// <summary>Identifies which scope limit (LLD §P2-H) was breached.</summary>
public enum ScopeLimit
{
    /// <summary><c>DiffScopeLimitOptions.MaxChangedFiles</c> — changed-file cap before push.</summary>
    MaxChangedFiles,

    /// <summary><c>DiffScopeLimitOptions.MaxChangedLines</c> — changed-line cap before push.</summary>
    MaxChangedLines,

    /// <summary><c>ScopeLimitOptions.MaxPRChangedFiles</c> — composite PR-size file cap.</summary>
    MaxPRChangedFiles,

    /// <summary><c>ScopeLimitOptions.MaxPRChangedLines</c> — composite PR-size line cap.</summary>
    MaxPRChangedLines
}

/// <summary>
/// Thrown when a task-scope limit is breached and the operation must halt rather than
/// proceed (e.g. a push whose diff exceeds <c>MaxChangedFiles</c>/<c>MaxChangedLines</c>).
/// Carries the breached <see cref="Limit"/>, the observed value, and the cap so callers
/// can surface an explanatory message.
/// </summary>
public sealed class ScopeLimitExceededException : Exception
{
    public ScopeLimitExceededException(ScopeLimit limit, int observed, int cap)
        : base($"Scope limit {limit} exceeded: observed {observed}, cap {cap}.")
    {
        Limit = limit;
        Observed = observed;
        Cap = cap;
    }

    /// <summary>The limit that was breached.</summary>
    public ScopeLimit Limit { get; }

    /// <summary>The observed value that exceeded the cap.</summary>
    public int Observed { get; }

    /// <summary>The configured cap.</summary>
    public int Cap { get; }
}
