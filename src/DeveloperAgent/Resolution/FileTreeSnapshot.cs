namespace DeveloperAgent.Resolution;

/// <summary>
/// Default <see cref="IWorkingTreeSnapshot"/>: a bounded, alphabetically-sorted listing of the working
/// tree's files as forward-slash relative paths, excluding build output and VCS internals. Cheap,
/// deterministic, and unit-testable — the model uses it as evidence of what is (and isn't) already in
/// the repository. Pure <see cref="System.IO"/>; no shell, no git, no library dependency.
/// </summary>
public sealed class FileTreeSnapshot : IWorkingTreeSnapshot
{
    /// <summary>Upper bound on listed entries so a huge repo cannot blow up the prompt.</summary>
    private const int MaxEntries = 600;

    private static readonly HashSet<string> ExcludedDirs =
        new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", "node_modules", ".vs", ".vscode" };

    public Task<string> CaptureAsync(string workspacePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            return Task.FromResult(string.Empty);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        var paths = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            IEnumerable<string> subDirs;
            IEnumerable<string> files;
            try
            {
                subDirs = Directory.EnumerateDirectories(dir);
                files = Directory.EnumerateFiles(dir);
            }
            catch (Exception)
            {
                // Unreadable directory (permissions, race) — skip it rather than fail the snapshot.
                continue;
            }

            foreach (var sub in subDirs)
            {
                var name = Path.GetFileName(sub);
                if (!ExcludedDirs.Contains(name))
                    pending.Push(sub);
            }

            foreach (var file in files)
                paths.Add(ToRelativeForwardSlash(root, file));
        }

        paths.Sort(StringComparer.OrdinalIgnoreCase);

        var capped = paths.Count > MaxEntries ? paths.GetRange(0, MaxEntries) : paths;
        var listing = string.Join("\n", capped);
        if (paths.Count > MaxEntries)
            listing += $"\n... ({paths.Count - MaxEntries} more files omitted)";

        return Task.FromResult(listing);
    }

    private static string ToRelativeForwardSlash(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        return relative.Replace(Path.DirectorySeparatorChar, '/').Replace('\\', '/');
    }
}
