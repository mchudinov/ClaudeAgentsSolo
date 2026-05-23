using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Sandbox;

/// <summary>
/// Default <see cref="IPathDenyPolicy"/>. Compiles <see cref="SandboxOptions"/>
/// into a set of regexes once at construction; <see cref="Check"/> is allocation-free.
/// </summary>
public sealed class PathDenyPolicy : IPathDenyPolicy
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly RegexOptions PathRegexOptions =
        OperatingSystem.IsWindows()
            ? RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
            : RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private static readonly char Sep = Path.DirectorySeparatorChar;
    private static readonly char AltSep = Path.AltDirectorySeparatorChar;

    private readonly IReadOnlyList<CompiledRule> _patternRules;
    private readonly IReadOnlyList<CompiledRule> _secretRules;

    public PathDenyPolicy(IOptions<SandboxOptions> options)
    {
        var opts = options.Value;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        _patternRules = opts.DenyPathPatterns
            .Select(p => CompileGlob(p, home))
            .ToArray();

        _secretRules = opts.SecretFileRegexes
            .Select(r => new CompiledRule(
                new Regex(r, PathRegexOptions),
                MatchTarget.Full,
                OriginalPattern:r))
            .ToArray();
    }

    public PathDenyResult Check(string absolutePath, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return PathDenyResult.Denied("empty path");

        var normalised = NormaliseAbsolute(absolutePath);
        var rootN = NormaliseAbsolute(workspaceRoot);

        // 1. Pattern-based deny — evaluated first so a denied path inside the
        //    workspace reports the matching pattern (e.g. ".env*") rather than
        //    the generic "outside workspace" message it would never produce.
        var hit = FirstMatch(_patternRules, normalised);
        if (hit is not null)
            return PathDenyResult.Denied($"path matches deny pattern '{hit}'");

        hit = FirstMatch(_secretRules, normalised);
        if (hit is not null)
            return PathDenyResult.Denied($"path matches secret-file regex '{hit}'");

        // 2. Workspace-escape — computed, not pattern-listed.
        if (!IsInsideWorkspace(normalised, rootN))
            return PathDenyResult.Denied($"path escapes workspace (outside root '{workspaceRoot}')");

        return PathDenyResult.Allowed;
    }

    private static string? FirstMatch(IReadOnlyList<CompiledRule> rules, string normalisedPath)
    {
        var basename = Path.GetFileName(normalisedPath);
        foreach (var rule in rules)
        {
            var target = rule.Target == MatchTarget.Basename ? basename : normalisedPath;
            if (target.Length == 0) continue;
            if (rule.Regex.IsMatch(target))
                return rule.OriginalPattern;
        }
        return null;
    }

    private static bool IsInsideWorkspace(string normalisedPath, string normalisedRoot)
    {
        if (string.IsNullOrEmpty(normalisedRoot)) return false;

        if (string.Equals(normalisedPath, normalisedRoot, PathComparison))
            return true;

        var rootWithSep = normalisedRoot.EndsWith(Sep)
            ? normalisedRoot
            : normalisedRoot + Sep;

        return normalisedPath.StartsWith(rootWithSep, PathComparison);
    }

    private static string NormaliseAbsolute(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;

        var full = Path.GetFullPath(path);
        // GetFullPath already normalises separators on each OS, but be defensive.
        if (AltSep != Sep)
            full = full.Replace(AltSep, Sep);
        return full.TrimEnd(Sep);
    }

    private static CompiledRule CompileGlob(string pattern, string home)
    {
        var expanded = ExpandHome(pattern, home);
        var hasSeparator = expanded.IndexOfAny(['/', '\\']) >= 0;

        // Normalise separators in the pattern to the host OS.
        var hostPattern = expanded.Replace('/', Sep);

        // A pattern with a separator is matched against the full path.
        // A pattern without (e.g. ".env*") matches the basename.
        var target = hasSeparator ? MatchTarget.Full : MatchTarget.Basename;

        // Patterns whose first character is a host-absolute marker (after `~`
        // expansion, the home path is absolute) must anchor at the path root.
        // Relative path patterns (e.g. ".git/config") match anywhere — either
        // at the path root or after a directory separator.
        var isAbsolute = hasSeparator && IsAbsoluteRoot(hostPattern);

        var regex = new Regex(
            GlobToRegex(hostPattern, anchorAtStart: !hasSeparator || isAbsolute),
            PathRegexOptions);
        return new CompiledRule(regex, target, OriginalPattern: pattern);
    }

    private static bool IsAbsoluteRoot(string hostPattern)
    {
        if (string.IsNullOrEmpty(hostPattern)) return false;
        if (OperatingSystem.IsWindows())
        {
            // "C:\..." or "\\server\share\..."
            return (hostPattern.Length >= 3 && char.IsLetter(hostPattern[0]) && hostPattern[1] == ':' && hostPattern[2] == Sep)
                || hostPattern.StartsWith("\\\\", StringComparison.Ordinal);
        }
        return hostPattern[0] == Sep;
    }

    private static string ExpandHome(string pattern, string home)
    {
        if (pattern.Length == 0 || pattern[0] != '~') return pattern;
        if (pattern.Length == 1) return home;
        var next = pattern[1];
        if (next == '/' || next == '\\')
            return home + pattern[1..];
        // "~user/..." is not supported — leave as-is so it cannot accidentally match.
        return pattern;
    }

    private static string GlobToRegex(string glob, bool anchorAtStart)
    {
        var sb = new System.Text.StringBuilder(glob.Length * 2 + 8);

        // When the pattern is not anchored at the path root, allow it to match
        // anywhere — either at the start, or after a directory separator.
        var sepEscaped = Regex.Escape(Sep.ToString());
        if (anchorAtStart)
            sb.Append('^');
        else
            sb.Append("(?:^|").Append(sepEscaped).Append(')');

        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    // "**" — match any number of characters including separators.
                    // Consume the second '*'. Also swallow a trailing separator so
                    // "a/**/b" matches "a/b" (zero-segment case).
                    i++;
                    if (i + 1 < glob.Length && (glob[i + 1] == Sep || glob[i + 1] == AltSep))
                    {
                        i++;
                        sb.Append("(?:.*").Append(sepEscaped).Append(")?");
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                }
                else
                {
                    // single '*' — any chars within a segment (no separator).
                    sb.Append("[^").Append(sepEscaped).Append("]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^").Append(sepEscaped).Append(']');
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        sb.Append('$');

        return sb.ToString();
    }

    private enum MatchTarget { Full, Basename }

    private sealed record CompiledRule(Regex Regex, MatchTarget Target, string OriginalPattern);
}
