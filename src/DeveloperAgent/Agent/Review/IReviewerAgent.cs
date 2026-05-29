using DeveloperAgent.GitHub;

namespace DeveloperAgent.Agent.Review;

/// <summary>
/// Reviews an open pull request and posts a single GitHub review (Approve or RequestChanges).
/// </summary>
/// <remarks>
/// The verdict combines deterministic checks (run in plain C#, no model call) with an
/// LLM-backed persona-violation scan. The two deterministic checks short-circuit before the
/// model is invoked:
/// <list type="number">
///   <item>the PR body is missing one of the canonical four sections → RequestChanges;</item>
///   <item>the diff is oversized (too many files or lines) → RequestChanges.</item>
/// </list>
/// Only a PR that passes both deterministic checks reaches the model-backed scan, which may
/// itself return Approve or RequestChanges. The single posting call is owned by C#.
/// </remarks>
public interface IReviewerAgent
{
    /// <summary>
    /// Reviews PR <paramref name="pullRequestNumber"/> and submits the resulting verdict to GitHub.
    /// </summary>
    Task<ReviewResult> ReviewAsync(int pullRequestNumber, CancellationToken ct);
}

/// <summary>The outcome of a review: the verdict posted and the summary that accompanied it.</summary>
/// <param name="Verdict">Approve or RequestChanges.</param>
/// <param name="Summary">The markdown body posted with the review.</param>
/// <param name="UsedModel">
/// True when the model-backed persona scan determined the verdict; false when a deterministic
/// check short-circuited the review before any model call.
/// </param>
public sealed record ReviewResult(ReviewVerdict Verdict, string Summary, bool UsedModel);
