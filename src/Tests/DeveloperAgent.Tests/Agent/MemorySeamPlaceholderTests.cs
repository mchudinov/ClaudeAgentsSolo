using Agent.Memory;
using DeveloperAgent.Agent.Memory;
using Microsoft.Extensions.AI;

namespace DeveloperAgent.Tests.Agent;

/// <summary>
/// Step-31: the host supplies non-LLM placeholder bodies for the two memory seams so the
/// providers can be wired end-to-end. The LLM-backed summarizer/extractor bodies are deferred
/// (see <c>docs/plans/07-phase-2-outline.md</c> §P2-G). These tests pin the placeholder contracts.
/// </summary>
public sealed class MemorySeamPlaceholderTests
{
    private static ChatMessage User(string t) => new(ChatRole.User, t);

    [Fact]
    public async Task PlaceholderSummarizer_returns_a_deterministic_non_empty_summary_without_a_model_call()
    {
        var summarizer = new PlaceholderSummarizer();

        var summary = await summarizer.SummarizeAsync([User("a"), User("b"), User("c")], CancellationToken.None);

        summary.Should().NotBeNullOrWhiteSpace();
        summary.Should().Contain("3", because: "the placeholder notes how many messages it compacted");

        // Deterministic: same input → same output (no model, no randomness).
        var again = await summarizer.SummarizeAsync([User("a"), User("b"), User("c")], CancellationToken.None);
        again.Should().Be(summary);
    }

    [Fact]
    public async Task NoOpMemoryExtractor_returns_Empty()
    {
        var extractor = new NoOpMemoryExtractor();

        var result = await extractor.ExtractAsync([User("a"), User("b")], CancellationToken.None);

        result.RepoMemories.Should().BeEmpty();
        result.TaskMemories.Should().BeEmpty();
    }
}
