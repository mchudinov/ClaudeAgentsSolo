using FluentAssertions;

namespace Agent.GitHub.Tests;

public sealed class MarkdownSectionBuilderTests
{
    // ── Build ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_renders_sections_in_order_with_blank_line_separators()
    {
        var result = MarkdownSectionBuilder.Build(
            ["## A", "## B", "## C"],
            ["one", "two", "three"]);

        // "{header}\n{content}\n" per section, a blank line between sections, single \n after the last.
        result.Should().Be("## A\none\n\n## B\ntwo\n\n## C\nthree\n");
    }

    [Fact]
    public void Build_renders_null_content_as_an_empty_line()
    {
        var result = MarkdownSectionBuilder.Build(["## A"], [null]);

        result.Should().Be("## A\n\n");
    }

    [Fact]
    public void Build_single_section_has_no_trailing_blank_line()
    {
        var result = MarkdownSectionBuilder.Build(["## Only"], ["body"]);

        result.Should().Be("## Only\nbody\n");
    }

    [Fact]
    public void Build_throws_when_header_and_content_counts_differ()
    {
        var act = () => MarkdownSectionBuilder.Build(["## A", "## B"], ["one"]);

        act.Should().Throw<ArgumentException>().WithParameterName("contents");
    }

    // ── FindMissingSections ────────────────────────────────────────────────────

    [Fact]
    public void FindMissingSections_returns_absent_headers_preserving_order()
    {
        var missing = MarkdownSectionBuilder.FindMissingSections(
            "## A\nx\n\n## C\ny",
            ["## A", "## B", "## C", "## D"]);

        missing.Should().Equal("## B", "## D");
    }

    [Fact]
    public void FindMissingSections_returns_empty_when_all_present()
    {
        var missing = MarkdownSectionBuilder.FindMissingSections(
            "## A\n## B\n## C",
            ["## A", "## B", "## C"]);

        missing.Should().BeEmpty();
    }

    [Fact]
    public void FindMissingSections_treats_null_body_as_all_missing()
    {
        var missing = MarkdownSectionBuilder.FindMissingSections(null, ["## A", "## B"]);

        missing.Should().Equal("## A", "## B");
    }
}
