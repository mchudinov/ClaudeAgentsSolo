using DeveloperAgent.Triage;

namespace DeveloperAgent.Tests.Triage;

public sealed class TriageCommentFormatterTests
{
    [Fact]
    public void Includes_the_reason()
    {
        var comment = TriageCommentFormatter.Format("This item targets a different repository.");

        comment.Should().Contain("This item targets a different repository.");
    }

    [Fact]
    public void States_it_was_auto_triaged_to_backlog()
    {
        var comment = TriageCommentFormatter.Format("Out of scope.");

        comment.Should().Contain("Backlog");
        comment.Should().Contain("triage");
    }

    [Fact]
    public void Explains_the_human_recovery_path_back_to_ready()
    {
        var comment = TriageCommentFormatter.Format("Out of scope.");

        comment.Should().Contain("Ready",
            because: "the comment is the human recovery path — it must say how to re-queue the item");
    }

    [Fact]
    public void Produces_a_clean_message_when_reason_is_blank()
    {
        var comment = TriageCommentFormatter.Format("   ");

        comment.Should().NotBeNullOrWhiteSpace();
        comment.Should().Contain("Backlog");
        comment.Should().NotContain("Reason:   ");
    }
}
