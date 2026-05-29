namespace DeveloperAgent.GitHub;

/// <summary>State of a GitHub Project v2 item on the board.</summary>
public enum ProjectState { Ready, InProgress, InReview, Done }

/// <summary>Aggregated review verdict for an open pull request.</summary>
public enum PullRequestReviewState { Pending, ChangesRequested, Approved }

/// <summary>
/// The verdict the reviewer agent submits on a pull request. Two values only — the task
/// posts Approve or RequestChanges; GitHub collapses any "reject" notion into the same
/// REQUEST_CHANGES event, so no third value is modelled here.
/// </summary>
public enum ReviewVerdict { Approve, RequestChanges }

/// <summary>
/// Everything the reviewer agent needs to review a pull request, fetched in one round-trip.
/// </summary>
/// <param name="Number">PR number in the repository.</param>
/// <param name="Body">The PR body markdown (used for the four-section check).</param>
/// <param name="ChangedFiles">Count of files changed in the PR.</param>
/// <param name="ChangedLines">Total changed lines (additions + deletions) across all files.</param>
/// <param name="UnifiedDiff">Concatenated per-file unified-diff patches (the change content).</param>
public sealed record PullRequestReviewContext(
    int Number,
    string Body,
    int ChangedFiles,
    int ChangedLines,
    string UnifiedDiff);

/// <summary>A GitHub Project v2 item that contains an Issue or Pull Request.</summary>
/// <param name="ProjectItemId">GraphQL node ID of the <c>ProjectV2Item</c>.</param>
/// <param name="ContentNodeId">Issue or PR node ID — used for comments.</param>
/// <param name="ContentNumber">Issue or PR number in the repository.</param>
/// <param name="Title">Issue or PR title.</param>
/// <param name="BodyMarkdown">Issue or PR body in markdown.</param>
/// <param name="State">Current status column on the project board.</param>
public sealed record ProjectItem(
    string ProjectItemId,
    string ContentNodeId,
    int ContentNumber,
    string Title,
    string BodyMarkdown,
    ProjectState State);

/// <summary>Parameters for creating a pull request.</summary>
/// <param name="HeadBranch">Source branch name (without owner prefix).</param>
/// <param name="BaseBranch">Target branch name.</param>
/// <param name="Title">PR title, ≤ 72 chars per persona §9.</param>
/// <param name="MarkdownBody">Already four-section-formatted body from <see cref="PullRequestBodyBuilder"/>.</param>
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
