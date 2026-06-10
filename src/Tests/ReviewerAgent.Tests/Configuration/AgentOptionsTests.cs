using Agent.Review;
using Microsoft.Extensions.Configuration;
using ReviewerAgent.Configuration;

namespace ReviewerAgent.Tests.Configuration;

public sealed class AgentOptionsTests
{
    [Fact]
    public void Defaults_match_documented_schema()
    {
        var options = new AgentOptions();
        options.Name.Should().Be("ReviewerAgent");
        options.Model.Should().Be("claude-opus-4-8");
        options.Effort.Should().Be("xhigh");
        options.PollIntervalSeconds.Should().Be(60);
        options.ReviewPollIntervalSeconds.Should().Be(60);
        options.FirstRetryIntervalSeconds.Should().Be(2);
    }

    [Fact]
    public void PersonaPath_default_is_relative()
    {
        var options = new AgentOptions();
        options.PersonaPath.Should().Be("personas/reviewer.md");
    }

    [Fact]
    public void Binding_from_Agent_section_overrides_defaults()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:Name"] = "TestReviewer",
                ["Agent:Model"] = "claude-3-5-sonnet",
                ["Agent:Effort"] = "high",
                ["Agent:PersonaPath"] = "personas/custom-reviewer.md",
                ["Agent:PollIntervalSeconds"] = "30",
            })
            .Build();

        var options = new AgentOptions();
        config.GetSection("Agent").Bind(options);

        options.Name.Should().Be("TestReviewer");
        options.Model.Should().Be("claude-3-5-sonnet");
        options.Effort.Should().Be("high");
        options.PersonaPath.Should().Be("personas/custom-reviewer.md");
        options.PollIntervalSeconds.Should().Be(30);
    }
}

/// <summary>
/// Guards the multi-section binding wired in <c>Program.cs</c>: the reviewer sources its
/// model/persona/poll-cadence from the shared <c>Agent</c> section (parity with the
/// DeveloperAgent host), its diff-size caps from <c>ScopeLimits</c> (the reviewer analog of the
/// DeveloperAgent host's <c>ScopeLimits</c> section), and its remaining review-specific knobs from
/// <c>Reviewer</c>. <see cref="ReviewerOptions"/> binds <c>Agent</c> → <c>Reviewer</c> →
/// <c>ScopeLimits</c> and <see cref="ReviewPollingOptions"/> binds <c>Agent</c> → <c>Reviewer</c>,
/// so this asserts each property resolves to the right source AND that list options are not
/// double-loaded (the Step-41 ConfigurationBinder append gotcha). The three sections are disjoint,
/// so the <c>RequiredPrBodySections</c> array (only in <c>Reviewer</c>) loads exactly once.
/// </summary>
public sealed class ReviewerMultiSectionBindingTests
{
    private static IConfiguration BuildConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Shared agent-identity section (mirrors the DeveloperAgent host).
            ["Agent:Model"] = "claude-opus-4-8",
            ["Agent:PersonaPath"] = "personas/reviewer.md",
            ["Agent:PollIntervalSeconds"] = "90",
            // Diff-size caps live in their own ScopeLimits section.
            ["ScopeLimits:MaxDiffFiles"] = "40",
            ["ScopeLimits:MaxDiffLines"] = "1500",
            // Review-specific knobs stay in Reviewer.
            ["Reviewer:RequiredPrBodySections:0"] = "## Summary",
            ["Reviewer:RequiredPrBodySections:1"] = "## User-visible behavior",
            ["Reviewer:RequiredPrBodySections:2"] = "## Tests/validation run",
            ["Reviewer:RequiredPrBodySections:3"] = "## Notes/assumptions",
            ["Reviewer:ReviewerLogin"] = "reviewer-bot",
            ["Reviewer:SkipDrafts"] = "false",
            ["Reviewer:AuthorAllowList:0"] = "dev-bot",
        })
        .Build();

    [Fact]
    public void ReviewerOptions_sources_model_from_Agent_diff_caps_from_ScopeLimits_and_knobs_from_Reviewer()
    {
        var config = BuildConfig();

        var options = new ReviewerOptions();
        // Same order Program.cs binds: Agent (Model, PersonaPath), Reviewer (knobs), ScopeLimits (diff caps).
        config.GetSection("Agent").Bind(options);
        config.GetSection("Reviewer").Bind(options);
        config.GetSection("ScopeLimits").Bind(options);

        options.Model.Should().Be("claude-opus-4-8");
        options.PersonaPath.Should().Be("personas/reviewer.md");
        options.MaxDiffFiles.Should().Be(40);
        options.MaxDiffLines.Should().Be(1500);

        // Step-41 gotcha guard: the four sections must load EXACTLY once, not append onto
        // a non-empty default and double up.
        options.RequiredPrBodySections.Should().HaveCount(4);
        options.RequiredPrBodySections.Should().ContainInOrder(
            "## Summary", "## User-visible behavior", "## Tests/validation run", "## Notes/assumptions");
    }

    [Fact]
    public void ReviewPollingOptions_sources_interval_from_Agent_and_knobs_from_Reviewer()
    {
        var config = BuildConfig();

        var options = new ReviewPollingOptions();
        config.GetSection("Agent").Bind(options);
        config.GetSection("Reviewer").Bind(options);

        options.PollIntervalSeconds.Should().Be(90);
        options.ReviewerLogin.Should().Be("reviewer-bot");
        options.SkipDrafts.Should().BeFalse();
        options.AuthorAllowList.Should().ContainSingle().Which.Should().Be("dev-bot");
    }
}
