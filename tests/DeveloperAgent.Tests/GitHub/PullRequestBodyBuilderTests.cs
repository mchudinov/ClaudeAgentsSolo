using DeveloperAgent.GitHub;
using FluentAssertions;

namespace DeveloperAgent.Tests.GitHub;

public sealed class PullRequestBodyBuilderTests
{
    // ── heading correctness ──────────────────────────────────────────────────

    [Fact]
    public void Build_contains_all_four_headings()
    {
        var body = PullRequestBodyBuilder.Build("Summary text", "Behavior", "Tests ran", "Notes");

        body.Should().Contain("## Summary");
        body.Should().Contain("## User-visible behavior");
        body.Should().Contain("## Tests/validation run");
        body.Should().Contain("## Notes/assumptions");
    }

    [Fact]
    public void Build_headings_appear_in_required_order()
    {
        var body = PullRequestBodyBuilder.Build("Summary text", "Behavior", "Tests ran", "Notes");

        var summaryIdx = body.IndexOf("## Summary", StringComparison.Ordinal);
        var behaviorIdx = body.IndexOf("## User-visible behavior", StringComparison.Ordinal);
        var testsIdx = body.IndexOf("## Tests/validation run", StringComparison.Ordinal);
        var notesIdx = body.IndexOf("## Notes/assumptions", StringComparison.Ordinal);

        summaryIdx.Should().BeLessThan(behaviorIdx);
        behaviorIdx.Should().BeLessThan(testsIdx);
        testsIdx.Should().BeLessThan(notesIdx);
    }

    // ── "None" for empty optional fields ────────────────────────────────────

    [Fact]
    public void Build_empty_userVisibleBehavior_renders_None()
    {
        var body = PullRequestBodyBuilder.Build("Summary text", "", "Tests ran", "Notes");

        body.Should().Contain("## User-visible behavior\nNone");
    }

    [Fact]
    public void Build_whitespace_only_userVisibleBehavior_renders_None()
    {
        var body = PullRequestBodyBuilder.Build("Summary text", "   ", "Tests ran", "Notes");

        body.Should().Contain("## User-visible behavior\nNone");
    }

    [Fact]
    public void Build_empty_testsValidationRun_renders_None()
    {
        var body = PullRequestBodyBuilder.Build("Summary text", "Behavior", "", "Notes");

        body.Should().Contain("## Tests/validation run\nNone");
    }

    [Fact]
    public void Build_empty_notesAssumptions_renders_None()
    {
        var body = PullRequestBodyBuilder.Build("Summary text", "Behavior", "Tests ran", "");

        body.Should().Contain("## Notes/assumptions\nNone");
    }

    [Fact]
    public void Build_null_optionalFields_render_None()
    {
        var body = PullRequestBodyBuilder.Build("Summary text", null!, null!, null!);

        body.Should().Contain("## User-visible behavior\nNone");
        body.Should().Contain("## Tests/validation run\nNone");
        body.Should().Contain("## Notes/assumptions\nNone");
    }

    // ── empty summary throws ─────────────────────────────────────────────────

    [Fact]
    public void Build_empty_summary_throws_ArgumentException()
    {
        var act = () => PullRequestBodyBuilder.Build("", "Behavior", "Tests ran", "Notes");

        act.Should().Throw<ArgumentException>()
           .WithMessage("*PR body Summary cannot be empty*")
           .WithParameterName("summary");
    }

    [Fact]
    public void Build_whitespace_only_summary_throws_ArgumentException()
    {
        var act = () => PullRequestBodyBuilder.Build("   ", "Behavior", "Tests ran", "Notes");

        act.Should().Throw<ArgumentException>()
           .WithParameterName("summary");
    }

    [Fact]
    public void Build_null_summary_throws_ArgumentException()
    {
        var act = () => PullRequestBodyBuilder.Build(null!, "Behavior", "Tests ran", "Notes");

        act.Should().Throw<ArgumentException>()
           .WithParameterName("summary");
    }

    // ── round-trip: inputs appear verbatim under correct headings ────────────

    [Fact]
    public void Build_roundtrip_all_inputs_appear_under_correct_headings()
    {
        const string summary = "Adds the frob mechanism";
        const string behavior = "POST /frob now returns 201";
        const string tests = "dotnet test → 42 passed";
        const string notes = "Deferred cleanup to step 4";

        var body = PullRequestBodyBuilder.Build(summary, behavior, tests, notes);

        // Each value appears after its own heading and before the next
        var summarySection = ExtractSection(body, "## Summary", "## User-visible behavior");
        var behaviorSection = ExtractSection(body, "## User-visible behavior", "## Tests/validation run");
        var testsSection = ExtractSection(body, "## Tests/validation run", "## Notes/assumptions");
        var notesSection = body[(body.IndexOf("## Notes/assumptions", StringComparison.Ordinal) + "## Notes/assumptions".Length)..];

        summarySection.Should().Contain(summary);
        behaviorSection.Should().Contain(behavior);
        testsSection.Should().Contain(tests);
        notesSection.Should().Contain(notes);
    }

    // ── heading text is exact (no spaces around slash) ───────────────────────

    [Fact]
    public void Build_heading_tests_has_no_spaces_around_slash()
    {
        var body = PullRequestBodyBuilder.Build("Summary", "Behavior", "Tests ran", "Notes");

        // Persona §9 uses "Tests/validation run" — no spaces around the slash
        body.Should().Contain("## Tests/validation run");
        body.Should().NotContain("## Tests / validation run");
    }

    [Fact]
    public void Build_heading_notes_has_no_spaces_around_slash()
    {
        var body = PullRequestBodyBuilder.Build("Summary", "Behavior", "Tests ran", "Notes");

        body.Should().Contain("## Notes/assumptions");
        body.Should().NotContain("## Notes / assumptions");
    }

    // ── helper ───────────────────────────────────────────────────────────────

    private static string ExtractSection(string body, string startHeading, string endHeading)
    {
        var start = body.IndexOf(startHeading, StringComparison.Ordinal) + startHeading.Length;
        var end = body.IndexOf(endHeading, StringComparison.Ordinal);
        return body[start..end];
    }
}
