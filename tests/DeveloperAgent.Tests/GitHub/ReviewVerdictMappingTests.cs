using DeveloperAgent.GitHub;
using FluentAssertions;
using Octokit;

namespace DeveloperAgent.Tests.GitHub;

/// <summary>
/// Locks the mapping from the agent-neutral <see cref="ReviewVerdict"/> onto Octokit's review
/// event, including the widened <see cref="ReviewVerdict.Comment"/> value (Step-35).
/// </summary>
public sealed class ReviewVerdictMappingTests
{
    [Theory]
    [InlineData(ReviewVerdict.Approve, PullRequestReviewEvent.Approve)]
    [InlineData(ReviewVerdict.RequestChanges, PullRequestReviewEvent.RequestChanges)]
    [InlineData(ReviewVerdict.Comment, PullRequestReviewEvent.Comment)]
    public void ToReviewEvent_maps_each_verdict_to_its_GitHub_event(
        ReviewVerdict verdict, PullRequestReviewEvent expected)
    {
        OctokitRestTransport.ToReviewEvent(verdict).Should().Be(expected);
    }
}
