namespace Agent.Tools;

/// <summary>
/// Validates and resolves file paths for file tools to ensure they stay within the workspace root
/// and clear of <see cref="IPathDenyPolicy"/> deny rules.
/// </summary>
public static class PathValidator
{
    /// <summary>
    /// Resolves <paramref name="workspaceRelative"/> relative to <paramref name="workspaceRoot"/>
    /// and asserts the result is inside (or equal to) the root.
    /// </summary>
    /// <param name="workspaceRelative">
    /// A relative path (e.g. <c>src/Foo.cs</c>) or an absolute path that must still be inside the root.
    /// </param>
    /// <param name="workspaceRoot">The absolute workspace root providing the boundary.</param>
    /// <returns>The fully resolved absolute path.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the resolved path escapes the workspace root (e.g. path traversal attempts like
    /// <c>../../etc/passwd</c> or absolute paths outside the root).
    /// </exception>
    public static string ResolveOrThrow(string workspaceRelative, string workspaceRoot)
    {
        string root = Path.GetFullPath(workspaceRoot);
        string rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        string resolved = Path.GetFullPath(Path.Combine(workspaceRoot, workspaceRelative));

        // Allow: path equals root exactly, or starts with root + separator
        if (!resolved.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !resolved.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("path escapes workspace");
        }

        return resolved;
    }

    /// <summary>
    /// Resolves <paramref name="workspaceRelative"/> against <paramref name="workspaceRoot"/> and
    /// runs the path through <paramref name="denyPolicy"/>. Returns <see langword="true"/>
    /// on allow with the absolute path in <paramref name="resolved"/>; on deny returns
    /// <see langword="false"/> with the human-readable reason in <paramref name="denyReason"/>.
    /// </summary>
    /// <remarks>
    /// This overload does not throw on workspace-escape — the deny policy reports it
    /// alongside the configurable pattern rules so callers can return a structured
    /// <see cref="ToolResult.Denied(string)"/>.
    /// </remarks>
    public static bool TryResolve(
        string workspaceRelative,
        string workspaceRoot,
        IPathDenyPolicy denyPolicy,
        out string resolved,
        out string denyReason)
    {
        // Path.GetFullPath collapses ".." and produces an OS-native absolute path.
        // We deliberately do this even for already-absolute inputs so that the deny
        // policy sees a canonical form.
        var absolute = Path.GetFullPath(Path.Combine(workspaceRoot, workspaceRelative));

        var result = denyPolicy.Check(absolute, workspaceRoot);
        if (result.IsDenied)
        {
            resolved = string.Empty;
            denyReason = result.Reason ?? "path denied";
            return false;
        }

        resolved = absolute;
        denyReason = string.Empty;
        return true;
    }
}
