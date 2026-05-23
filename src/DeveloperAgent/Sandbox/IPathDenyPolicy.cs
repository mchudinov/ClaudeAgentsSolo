namespace DeveloperAgent.Sandbox;

/// <summary>
/// Outcome of a <see cref="IPathDenyPolicy.Check(string, string)"/> evaluation.
/// </summary>
/// <param name="IsDenied">
/// <see langword="true"/> when the path is rejected (workspace-escape or pattern hit).
/// </param>
/// <param name="Reason">
/// Human-readable reason describing which rule fired. <see langword="null"/> on allow.
/// </param>
public readonly record struct PathDenyResult(bool IsDenied, string? Reason)
{
    public static PathDenyResult Allowed { get; } = new(false, null);
    public static PathDenyResult Denied(string reason) => new(true, reason);
}

/// <summary>
/// Path-deny gate consulted by the file tools and the command sandbox before
/// reading, writing, or running commands against a filesystem path.
/// </summary>
public interface IPathDenyPolicy
{
    /// <summary>
    /// Evaluates the deny rules against <paramref name="absolutePath"/>.
    /// </summary>
    /// <param name="absolutePath">
    /// An absolute, OS-native path. Callers are expected to have already resolved
    /// any relative input through <see cref="Path.GetFullPath(string)"/>.
    /// </param>
    /// <param name="workspaceRoot">
    /// The workspace boundary. Any absolute path that is not equal to, or beneath,
    /// this root is denied as a workspace escape.
    /// </param>
    PathDenyResult Check(string absolutePath, string workspaceRoot);
}
