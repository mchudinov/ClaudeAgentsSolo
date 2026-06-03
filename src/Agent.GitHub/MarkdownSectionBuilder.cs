using System.Text;

namespace Agent.GitHub;

/// <summary>
/// Agent-neutral mechanics for markdown documents built from a fixed sequence of
/// <c>## Header</c> + content sections: renders such a document, and reports which required
/// headers are absent from an existing body. The <em>section policy</em> (which headers, in
/// what order, with what defaulting) lives with the caller — this type only renders and detects.
/// </summary>
public static class MarkdownSectionBuilder
{
    /// <summary>
    /// Renders <paramref name="headers"/> and <paramref name="contents"/> as markdown sections,
    /// one per header in order. Each section is <c>"{header}\n{content}\n"</c> with a single blank
    /// line between consecutive sections. Newlines are emitted as explicit <c>\n</c> so the result
    /// is byte-identical regardless of the source file's line endings.
    /// </summary>
    /// <param name="headers">Section headers, in order. Must be the same length as <paramref name="contents"/>.</param>
    /// <param name="contents">Per-section content; a null entry renders as an empty line.</param>
    /// <exception cref="ArgumentException">Thrown when the two lists differ in length.</exception>
    public static string Build(IReadOnlyList<string> headers, IReadOnlyList<string?> contents)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(contents);
        if (headers.Count != contents.Count)
            throw new ArgumentException(
                $"headers ({headers.Count}) and contents ({contents.Count}) must have the same length.",
                nameof(contents));

        var sb = new StringBuilder();
        for (var i = 0; i < headers.Count; i++)
        {
            sb.Append(headers[i]).Append('\n').Append(contents[i] ?? string.Empty).Append('\n');
            if (i < headers.Count - 1)
                sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Returns the entries of <paramref name="headers"/> that do not appear as an ordinal substring
    /// of <paramref name="body"/>, preserving the input order. An empty result means every required
    /// header is present.
    /// </summary>
    public static IReadOnlyList<string> FindMissingSections(string? body, IReadOnlyList<string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var present = body ?? string.Empty;
        return headers.Where(h => !present.Contains(h, StringComparison.Ordinal)).ToList();
    }
}
