using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Agent.Workspace;

/// <summary>
/// Builds the agent's git branch name, following the naming rules in personas/developer.md §7.
/// </summary>
public static class BranchName
{
    // Prefix occupies 6 chars; trailing "-hhhhhhhh" occupies 9 chars (hyphen + 8 hex).
    // Remaining slug budget: 40 - 6 - 9 = 25 chars.
    private const int TotalMaxLength = 40;
    private const string Prefix = "agent/";
    private const int PrefixLength = 6; // "agent/".Length
    private const int HashSuffixLength = 9; // "-" + 8 hex chars
    private const int SlugMaxLength = TotalMaxLength - PrefixLength - HashSuffixLength; // 25

    private static readonly Regex MultipleHyphens = new(@"-{2,}", RegexOptions.Compiled);

    /// <summary>
    /// Returns the branch name for a task.
    /// <list type="bullet">
    ///   <item>If <paramref name="threadId"/> is non-empty: <c>agent/{threadId[..8]}</c>.</item>
    ///   <item>
    ///     Otherwise: <c>agent/{slug(taskTitle)}-{shortHash(taskTitle)}</c> where:
    ///     <list type="bullet">
    ///       <item>slug = lowercase ASCII letters/digits with hyphens for spaces, all other chars stripped;</item>
    ///       <item>total length ≤ 40 chars;</item>
    ///       <item>trailing hyphens trimmed.</item>
    ///     </list>
    ///   </item>
    /// </list>
    /// </summary>
    /// <param name="threadId">Optional agent thread identifier (e.g. a ULID/UUID string).</param>
    /// <param name="taskTitle">Human-readable task title. Must not be null, empty, or whitespace.</param>
    /// <returns>An <c>agent/</c>-prefixed branch name safe for use as a git ref.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="taskTitle"/> is null, empty, or whitespace.</exception>
    public static string ForTask(string? threadId, string taskTitle)
    {
        if (string.IsNullOrWhiteSpace(taskTitle))
            throw new ArgumentException("Task title must not be null, empty, or whitespace.", nameof(taskTitle));

        if (!string.IsNullOrEmpty(threadId))
        {
            var id = threadId.Length >= 8 ? threadId[..8] : threadId;
            return Prefix + id;
        }

        var hash = ComputeShortHash(taskTitle);
        var slug = Slugify(taskTitle, SlugMaxLength);
        return Prefix + slug + "-" + hash;
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private static string Slugify(string title, int maxLength)
    {
        // 1. Lowercase
        var s = title.ToLowerInvariant();

        // 2. Replace spaces with hyphens; strip all non-ASCII-alphanumeric characters except hyphens
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                sb.Append(c);
            else if (c == ' ')
                sb.Append('-');
            // all other chars (punctuation, non-ASCII) are stripped
        }

        var raw = sb.ToString();

        // 3. Collapse consecutive hyphens introduced by stripped punctuation between spaces
        raw = MultipleHyphens.Replace(raw, "-");

        // 4. Truncate to max slug length
        if (raw.Length > maxLength)
            raw = raw[..maxLength];

        // 5. Trim trailing hyphens (truncation or stripped punctuation can leave them)
        raw = raw.TrimEnd('-');

        return raw;
    }

    private static string ComputeShortHash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        // First 4 bytes → 8 hex chars
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }
}
