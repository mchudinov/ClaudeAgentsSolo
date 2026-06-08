namespace Agent.GitHub;

/// <summary>Aggregated review verdict for an open pull request.</summary>
public enum PullRequestReviewState { Pending, ChangesRequested, Approved }

/// <summary>
/// The verdict submitted on a pull request review, mirroring GitHub's three submittable review
/// events: <see cref="Approve"/> (APPROVE), <see cref="RequestChanges"/> (REQUEST_CHANGES) and
/// <see cref="Comment"/> (COMMENT). A consumer may restrict itself to a subset (e.g. the
/// developer-agent reviewer uses only Approve/RequestChanges as a binary gate).
/// </summary>
public enum ReviewVerdict { Approve, RequestChanges, Comment }

/// <summary>
/// Everything needed to review a pull request, fetched in one round-trip.
/// </summary>
/// <param name="Number">PR number in the repository.</param>
/// <param name="Body">The PR body markdown.</param>
/// <param name="ChangedFiles">Count of files changed in the PR.</param>
/// <param name="ChangedLines">Total changed lines (additions + deletions) across all files.</param>
/// <param name="UnifiedDiff">Concatenated per-file unified-diff patches (the change content).</param>
public sealed record PullRequestReviewContext(
    int Number,
    string Body,
    int ChangedFiles,
    int ChangedLines,
    string UnifiedDiff);

/// <summary>
/// A GitHub Project v2 item with its board column as a raw status name (e.g. "Ready", "In Review").
/// This is the agent-neutral unit the client returns; mapping it onto a lifecycle-state type is the
/// consuming app's concern.
/// </summary>
/// <param name="ProjectItemId">GraphQL node ID of the <c>ProjectV2Item</c>.</param>
/// <param name="ContentNodeId">Issue or PR node ID — used for comments.</param>
/// <param name="ContentNumber">Issue or PR number in the repository (0 for draft issues).</param>
/// <param name="Title">Issue or PR title.</param>
/// <param name="BodyMarkdown">Issue or PR body in markdown.</param>
/// <param name="Status">Current status-column name on the project board.</param>
public sealed record ProjectBoardItem(
    string ProjectItemId,
    string ContentNodeId,
    int ContentNumber,
    string Title,
    string BodyMarkdown,
    string Status);

/// <summary>Parameters for creating a pull request.</summary>
/// <param name="HeadBranch">Source branch name (without owner prefix).</param>
/// <param name="BaseBranch">Target branch name.</param>
/// <param name="Title">PR title.</param>
/// <param name="MarkdownBody">Already-formatted markdown body supplied by the caller (e.g. the developer agent's PR template).</param>
public sealed record CreatePullRequest(
    string HeadBranch,
    string BaseBranch,
    string Title,
    string MarkdownBody);

/// <summary>A GitHub pull request.</summary>
/// <param name="Number">PR number in the repository.</param>
/// <param name="HeadSha">HEAD commit SHA of the head branch at the time this was fetched.</param>
/// <param name="HtmlUrl">Browser URL for the pull request.</param>
public sealed record PullRequest(
    int Number,
    string HeadSha,
    string HtmlUrl);

/// <summary>Combined status snapshot for a pull request.</summary>
/// <param name="Number">PR number.</param>
/// <param name="Review">Aggregated review verdict (most recent non-dismissed review per reviewer).</param>
/// <param name="ChecksGreen">True when every check conclusion is in {success, neutral, skipped}.</param>
/// <param name="Merged">True when the PR has been merged.</param>
/// <param name="HeadSha">HEAD commit SHA of the head branch at the time this was fetched.</param>
public sealed record PullRequestStatus(
    int Number,
    PullRequestReviewState Review,
    bool ChecksGreen,
    bool Merged,
    string HeadSha);

/// <summary>An open pull request as surfaced for review scheduling.</summary>
/// <param name="Number">PR number in the repository.</param>
/// <param name="HeadSha">HEAD commit SHA of the head branch at fetch time.</param>
/// <param name="IsDraft">True when the PR is a draft (not ready for review).</param>
/// <param name="Author">Login of the user who opened the PR.</param>
/// <param name="HtmlUrl">Browser URL for the pull request.</param>
public sealed record OpenPullRequest(
    int Number,
    string HeadSha,
    bool IsDraft,
    string Author,
    string HtmlUrl);
