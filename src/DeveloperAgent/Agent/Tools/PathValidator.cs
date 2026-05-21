using DeveloperAgent.Workspace;

namespace DeveloperAgent.Agent.Tools;

/// <summary>
/// Validates and resolves file paths for file tools to ensure they stay within the workspace root.
/// </summary>
internal static class PathValidator
{
    /// <summary>
    /// Resolves <paramref name="workspaceRelative"/> relative to <paramref name="ws"/>.<see cref="TaskWorkspace.RepoRoot"/>
    /// and asserts the result is inside (or equal to) <c>RepoRoot</c>.
    /// </summary>
    /// <param name="workspaceRelative">
    /// A relative path (e.g. <c>src/Foo.cs</c>) or an absolute path that must still be inside <c>RepoRoot</c>.
    /// </param>
    /// <param name="ws">The task workspace providing the root boundary.</param>
    /// <returns>The fully resolved absolute path.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the resolved path escapes the workspace root (e.g. path traversal attempts like
    /// <c>../../etc/passwd</c> or absolute paths outside <c>RepoRoot</c>).
    /// </exception>
    internal static string ResolveOrThrow(string workspaceRelative, TaskWorkspace ws)
    {
        string root = Path.GetFullPath(ws.RepoRoot);
        string rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        string resolved = Path.GetFullPath(Path.Combine(ws.RepoRoot, workspaceRelative));

        // Allow: path equals root exactly, or starts with root + separator
        if (!resolved.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !resolved.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("path escapes workspace");
        }

        return resolved;
    }
}
