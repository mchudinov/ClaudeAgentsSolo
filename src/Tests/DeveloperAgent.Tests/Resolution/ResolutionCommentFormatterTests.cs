using DeveloperAgent.Resolution;

namespace DeveloperAgent.Tests.Resolution;

public sealed class ResolutionCommentFormatterTests
{
    [Fact]
    public void Includes_the_reason()
    {
        var comment = ResolutionCommentFormatter.Format("The MergePullRequestActivity class already exists in src/.");

        comment.Should().Contain("The MergePullRequestActivity class already exists in src/.");
    }

    [Fact]
    public void States_the_item_appears_already_implemented_and_was_returned_to_backlog()
    {
        var comment = ResolutionCommentFormatter.Format("Already done.");

        comment.Should().Contain("Backlog");
        comment.Should().ContainEquivalentOf("already");
    }

    [Fact]
    public void Directs_the_human_to_mark_Done_rather_than_re_queue_to_Ready()
    {
        // The actionable distinction from the triage/failure park comments: if the work really is
        // done, a maintainer should mark the item Done — NOT move it back to Ready, which would just
        // re-trigger this same check. The comment must steer toward Done.
        var comment = ResolutionCommentFormatter.Format("Already implemented.");

        comment.Should().Contain("Done",
            because: "an already-implemented item should be confirmed and marked Done, not re-queued");
    }

    [Fact]
    public void Produces_a_clean_message_when_reason_is_blank()
    {
        var comment = ResolutionCommentFormatter.Format("   ");

        comment.Should().NotBeNullOrWhiteSpace();
        comment.Should().Contain("Backlog");
        comment.Should().NotContain("Reason:   ");
    }
}
