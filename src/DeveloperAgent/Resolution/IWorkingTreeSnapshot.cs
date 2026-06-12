namespace DeveloperAgent.Resolution;

/// <summary>
/// Captures a bounded, read-only textual snapshot of a checked-out working tree for the already-resolved
/// checker to reason over. A swappable seam: the default <see cref="FileTreeSnapshot"/> lists source
/// files, but a richer implementation could add git history, a keyword grep, or file excerpts.
/// </summary>
public interface IWorkingTreeSnapshot
{
    /// <summary>
    /// Returns a textual snapshot of the working tree rooted at <paramref name="workspacePath"/>, or an
    /// empty string when the path is blank/missing. Must never throw — IO failures resolve to "".
    /// </summary>
    Task<string> CaptureAsync(string workspacePath, CancellationToken ct);
}
