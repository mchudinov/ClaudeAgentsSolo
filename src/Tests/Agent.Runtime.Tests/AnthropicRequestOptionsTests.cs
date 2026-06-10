using Agent.Runtime;
using Anthropic.Models.Messages;
using FluentAssertions;
using Xunit;

namespace Agent.Runtime.Tests;

/// <summary>
/// Verifies the agent-neutral effort → Anthropic request seam. Both the developer runner and the
/// reviewer engine set <c>ChatOptions.RawRepresentationFactory</c> to the delegate this produces;
/// the Anthropic <c>AsIChatClient</c> adapter invokes it to seed the request, so effort lands in
/// <c>output_config.effort</c>.
/// </summary>
public sealed class AnthropicRequestOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_effort_yields_no_factory(string? effort)
    {
        AnthropicRequestOptions.EffortFactory(effort).Should().BeNull(
            because: "a blank effort must leave the request untouched (provider default)");
    }

    [Theory]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("high", "high")]
    [InlineData("xhigh", "xhigh")]
    [InlineData("max", "max")]
    [InlineData("XHigh", "xhigh")]   // case-insensitive
    [InlineData(" high ", "high")]   // trimmed
    public void Maps_effort_onto_output_config(string configured, string expected)
    {
        var factory = AnthropicRequestOptions.EffortFactory(configured);
        factory.Should().NotBeNull();

        var seed = factory!(null!);
        var prams = seed.Should().BeOfType<MessageCreateParams>().Subject;
        prams.OutputConfig.Should().NotBeNull();
        // OutputConfig.Effort is ApiEnum<string, Effort>; the implicit ApiEnum→string conversion
        // returns the raw wire value ("xhigh" etc.) regardless of whether it is a named member.
        string rawEffort = prams.OutputConfig!.Effort!;
        rawEffort.Should().Be(expected);
    }
}
