using Agent.GitHub;

namespace Agent.Review;

/// <summary>
/// Reviews an open pull request and posts a single GitHub review (Approve or RequestChanges).
/// </summary>
/// <remarks>
/// The verdict combines deterministic checks (plain C#, no model call) with an LLM-backed
/// persona-violation scan. Two deterministic checks short-circuit before the model is invoked:
/// (1) the PR body is missing a required section → RequestChanges; (2) the diff is oversized
/// → RequestChanges. Only a PR that passes both reaches the model-backed scan. Never merges.
/// </remarks>
public interface IReviewerAgent
{
    /// <summary>Reviews PR <paramref name="pullRequestNumber"/> and submits the verdict to GitHub.</summary>
    Task<ReviewResult> ReviewAsync(int pullRequestNumber, CancellationToken ct);
}

/// <summary>The outcome of a review: the verdict posted and the summary that accompanied it.</summary>
/// <param name="Verdict">Approve or RequestChanges.</param>
/// <param name="Summary">The markdown body posted with the review.</param>
/// <param name="UsedModel">True when the model-backed scan set the verdict; false when a deterministic check short-circuited.</param>
public sealed record ReviewResult(ReviewVerdict Verdict, string Summary, bool UsedModel);
