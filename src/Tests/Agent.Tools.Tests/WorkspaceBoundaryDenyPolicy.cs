namespace Agent.Tools.Tests;

/// <summary>
/// A faithful <see cref="IPathDenyPolicy"/> test double enforcing only the workspace-escape rule —
/// the part of the contract the generic file tools depend on. A path equal to or beneath the root
/// is allowed; anything else is denied with a reason containing the substring "escapes workspace".
/// The host's configurable glob/secret deny rules live in Agent.Sandbox's PathDenyPolicy and are
/// tested there; this double keeps the moved file-tool tests free of any host dependency.
/// </summary>
internal sealed class WorkspaceBoundaryDenyPolicy : IPathDenyPolicy
{
    public PathDenyResult Check(string absolutePath, string workspaceRoot)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(absolutePath);

        var inside = full.Equals(root, StringComparison.OrdinalIgnoreCase)
                     || full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);

        return inside
            ? PathDenyResult.Allowed
            : PathDenyResult.Denied($"path escapes workspace (outside root '{workspaceRoot}')");
    }
}
