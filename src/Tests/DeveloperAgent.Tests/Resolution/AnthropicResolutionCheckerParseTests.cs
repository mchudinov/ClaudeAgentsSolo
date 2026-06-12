using DeveloperAgent.Configuration;
using DeveloperAgent.Resolution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Tests.Resolution;

/// <summary>
/// Pins the <b>safety contract</b> of <see cref="AnthropicResolutionChecker"/>: the verdict parse is
/// fail-open in the conservative direction — any empty/malformed/missing/unexpected response resolves to
/// <c>IsAlreadyResolved == false</c> (proceed to implement), the OPPOSITE direction from relevance triage.
/// A regression here (e.g. flipping the default to <c>true</c>, or copy-pasting triage's fail-open) would
/// silently abandon real work to the write-only Backlog column, which is unrecoverable.
/// </summary>
public sealed class AnthropicResolutionCheckerParseTests
{
    private static AnthropicResolutionChecker Checker() =>
        new(
            Substitute.For<IAgentChatClientFactory>(),
            Substitute.For<IWorkingTreeSnapshot>(),
            Options.Create(new AgentOptions()),
            NullLogger<AnthropicResolutionChecker>.Instance);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("there is no json object here")]
    [InlineData("{ this is not valid json }")]
    [InlineData("{\"reason\":\"missing the alreadyResolved field\"}")]
    [InlineData("{\"alreadyResolved\":\"banana\"}")]
    public void Parse_fails_open_to_not_resolved_on_any_bad_or_uncertain_response(string? text)
    {
        Checker().Parse(text).IsAlreadyResolved.Should().BeFalse(
            because: "a false 'already resolved' abandons real work unrecoverably, so anything uncertain must proceed");
    }

    [Fact]
    public void Parse_returns_resolved_only_on_an_explicit_true_verdict()
    {
        var verdict = Checker().Parse("{\"alreadyResolved\": true, \"reason\": \"WidgetService already exists in src/Services.\"}");

        verdict.IsAlreadyResolved.Should().BeTrue();
        verdict.Reason.Should().Contain("WidgetService already exists in src/Services.");
    }

    [Fact]
    public void Parse_honours_an_explicit_false_verdict()
    {
        Checker().Parse("{\"alreadyResolved\": false, \"reason\": \"not implemented yet\"}")
            .IsAlreadyResolved.Should().BeFalse();
    }
}
