using Agent.Workspace;
using FluentAssertions;

namespace Agent.Workspace.Tests;

public sealed class BranchNameTests
{
    // ── Thread-id path ───────────────────────────────────────────────────────

    [Fact]
    public void ForTask_threadId_returns_first_8_chars_with_prefix()
    {
        var result = BranchName.ForTask("d3f12e9c8b7a6510", "Any title");

        result.Should().Be("agent/d3f12e9c");
    }

    [Fact]
    public void ForTask_threadId_shorter_than_8_uses_whole_id()
    {
        // A short thread ID should not throw; uses the entire string
        var result = BranchName.ForTask("abc", "Any title");

        result.Should().Be("agent/abc");
    }

    [Fact]
    public void ForTask_threadId_exactly_8_chars_uses_whole_id()
    {
        var result = BranchName.ForTask("12345678", "Any title");

        result.Should().Be("agent/12345678");
    }

    [Fact]
    public void ForTask_empty_threadId_falls_through_to_slug_path()
    {
        var result = BranchName.ForTask("", "Add user profile endpoint");

        result.Should().StartWith("agent/add-user-profile-endpoint-");
    }

    [Fact]
    public void ForTask_null_threadId_falls_through_to_slug_path()
    {
        var result = BranchName.ForTask(null, "Add user profile endpoint");

        result.Should().StartWith("agent/add-user-profile-endpoint-");
    }

    // ── Slug path ────────────────────────────────────────────────────────────

    [Fact]
    public void ForTask_slug_path_starts_with_lowercased_title_words()
    {
        var result = BranchName.ForTask(null, "Add user profile endpoint");

        result.Should().StartWith("agent/add-user-profile-endpoint-");
    }

    [Fact]
    public void ForTask_slug_path_ends_with_8_hex_chars()
    {
        var result = BranchName.ForTask(null, "Add user profile endpoint");

        // Last 8 chars should be hex
        var suffix = result[^8..];
        suffix.Should().MatchRegex("^[0-9a-f]{8}$");
    }

    [Fact]
    public void ForTask_slug_path_total_length_le_40()
    {
        var result = BranchName.ForTask(null, "Add user profile endpoint");

        result.Length.Should().BeLessThanOrEqualTo(40);
    }

    // ── Punctuation stripping ────────────────────────────────────────────────

    [Fact]
    public void ForTask_strips_hash_em_dash_and_exclamation()
    {
        var result = BranchName.ForTask(null, "Fix bug #42 — auth!");

        // No #, no —, no !
        result.Should().NotContain("#");
        result.Should().NotContain("—");
        result.Should().NotContain("!");
    }

    [Fact]
    public void ForTask_slug_is_lowercase_ascii_hyphens_only()
    {
        var result = BranchName.ForTask(null, "Fix: Multiple Issues (Auth & DB)");

        // After "agent/" prefix, everything should be lowercase alphanumeric or hyphen
        var afterPrefix = result["agent/".Length..];
        afterPrefix.Should().MatchRegex("^[a-z0-9\\-]+$");
    }

    [Fact]
    public void ForTask_non_ascii_chars_are_stripped()
    {
        var result = BranchName.ForTask(null, "Add Ünïcödé feature");

        // Non-ASCII characters are stripped, not transliterated
        var afterPrefix = result["agent/".Length..];
        afterPrefix.Should().MatchRegex("^[a-z0-9\\-]+$");
    }

    // ── Length cap ───────────────────────────────────────────────────────────

    [Fact]
    public void ForTask_200_char_title_produces_output_le_40_chars()
    {
        var longTitle = new string('a', 200);

        var result = BranchName.ForTask(null, longTitle);

        result.Length.Should().BeLessThanOrEqualTo(40);
    }

    [Fact]
    public void ForTask_long_title_with_spaces_still_le_40_chars()
    {
        var longTitle = string.Join(" ", Enumerable.Repeat("word", 50));

        var result = BranchName.ForTask(null, longTitle);

        result.Length.Should().BeLessThanOrEqualTo(40);
    }

    // ── Trailing hyphen trimming ─────────────────────────────────────────────

    [Fact]
    public void ForTask_title_ending_with_spaces_does_not_produce_trailing_hyphen_before_hash()
    {
        // Spaces at end become hyphens → must be trimmed before appending hash
        var result = BranchName.ForTask(null, "foo   ");

        // The part between "agent/" prefix and the last "-hhhhhhhh" should not end in hyphen
        var afterPrefix = result["agent/".Length..];
        var lastHyphen = afterPrefix.LastIndexOf('-');
        var slugPart = afterPrefix[..lastHyphen];
        slugPart.Should().NotEndWith("-");
    }

    [Fact]
    public void ForTask_title_that_truncates_mid_word_does_not_leave_trailing_hyphen()
    {
        // Create a title that when slugified and truncated to 25 chars ends with a hyphen
        // "aaaaaaaaaaaaaaaaaaaaaaaaa-b" → truncate at 25 gives "aaaaaaaaaaaaaaaaaaaaaaaaa-" → trim → "aaaaaaaaaaaaaaaaaaaaaaaaa"
        var title = new string('a', 25) + "-b";

        var result = BranchName.ForTask(null, title);

        result.Should().NotContain("--");
        var afterPrefix = result["agent/".Length..];
        var lastHyphen = afterPrefix.LastIndexOf('-');
        var slugPart = afterPrefix[..lastHyphen];
        slugPart.Should().NotEndWith("-");
    }

    // ── Determinism ──────────────────────────────────────────────────────────

    [Fact]
    public void ForTask_same_title_produces_same_output_every_time()
    {
        const string title = "Add user profile endpoint";

        var first = BranchName.ForTask(null, title);
        var second = BranchName.ForTask(null, title);

        first.Should().Be(second);
    }

    [Fact]
    public void ForTask_different_titles_produce_different_hashes()
    {
        var a = BranchName.ForTask(null, "Feature A");
        var b = BranchName.ForTask(null, "Feature B");

        a.Should().NotBe(b);
    }

    // ── Error cases ──────────────────────────────────────────────────────────

    [Fact]
    public void ForTask_empty_title_throws_ArgumentException()
    {
        var act = () => BranchName.ForTask(null, "");

        act.Should().Throw<ArgumentException>()
           .WithParameterName("taskTitle");
    }

    [Fact]
    public void ForTask_whitespace_only_title_throws_ArgumentException()
    {
        var act = () => BranchName.ForTask(null, "   ");

        act.Should().Throw<ArgumentException>()
           .WithParameterName("taskTitle");
    }

    [Fact]
    public void ForTask_null_title_throws_ArgumentException()
    {
        var act = () => BranchName.ForTask(null, null!);

        act.Should().Throw<ArgumentException>()
           .WithParameterName("taskTitle");
    }

    // ── Prefix ───────────────────────────────────────────────────────────────

    [Fact]
    public void ForTask_always_starts_with_agent_prefix()
    {
        var resultThreadId = BranchName.ForTask("abc12345", "title");
        var resultSlug = BranchName.ForTask(null, "title");

        resultThreadId.Should().StartWith("agent/");
        resultSlug.Should().StartWith("agent/");
    }
}
