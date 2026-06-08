namespace Agent.GitHub;

/// <summary>
/// Agent-neutral client for the deterministic GitHub Projects v2 + pull-request operations any
/// board-driven agent needs. Board operations are keyed by status-column <em>name</em> (not a fixed
/// state enum), so a fixed lifecycle (e.g. the four-column developer-agent flow) is policy that
/// lives in the consuming app, not in this client.
/// </summary>
public interface IGitHubProjectsClient
{
    /// <summary>
    /// Returns the next project item whose status column is <paramref name="statusName"/>, or
    /// <see langword="null"/> if none. Selection order: by position in that column (top first).
    /// </summary>
    Task<ProjectBoardItem?> TryGetNextItemInStatusAsync(string statusName, CancellationToken ct);

    /// <summary>
    /// Sets a project item's status column from <paramref name="fromStatusName"/> to
    /// <paramref name="toStatusName"/>. When the two names are equal this is a no-op (the mutation is
    /// skipped).
    /// </summary>
    Task MoveItemAsync(string projectItemId, string fromStatusName, string toStatusName, CancellationToken ct);

    /// <summary>Posts a markdown comment on the underlying issue/PR node of the project item.</summary>
    Task AddItemCommentAsync(string contentNodeId, string markdownBody, CancellationToken ct);

    /// <summary>
    /// Creates a pull request from <paramref name="request"/>. If a PR already exists for the head
    /// branch (422 already_exists), returns the existing PR instead of throwing.
    /// </summary>
    Task<PullRequest> CreatePullRequestAsync(CreatePullRequest request, CancellationToken ct);

    /// <summary>Returns a combined status snapshot for the pull request (merge/review/checks).</summary>
    Task<PullRequestStatus> GetPullRequestStatusAsync(int pullRequestNumber, CancellationToken ct);

    /// <summary>Fetches the PR body, changed-file/line counts, and concatenated unified diff in one round-trip.</summary>
    Task<PullRequestReviewContext> GetPullRequestForReviewAsync(int pullRequestNumber, CancellationToken ct);

    /// <summary>Submits a review on the pull request with the given verdict and markdown body. Never merges.</summary>
    Task SubmitReviewAsync(int pullRequestNumber, ReviewVerdict verdict, string body, CancellationToken ct);

    /// <summary>
    /// Fetches review-thread comments and issue comments newer than <paramref name="sinceUtc"/>,
    /// concatenated as a single markdown blob (empty string when none are newer).
    /// </summary>
    Task<string> GetReviewFeedbackSinceAsync(int pullRequestNumber, DateTimeOffset sinceUtc, CancellationToken ct);

    /// <summary>Returns items currently in any of the given status columns.</summary>
    Task<IReadOnlyList<ProjectBoardItem>> GetItemsInStatusesAsync(IReadOnlyCollection<string> statusNames, CancellationToken ct);

    /// <summary>Returns the number of items currently in the given status column.</summary>
    Task<int> GetItemCountInStatusAsync(string statusName, CancellationToken ct);

    /// <summary>Returns true if the configured repository exists and is accessible on GitHub.</summary>
    Task<bool> RepositoryExistsAsync(CancellationToken ct);

    /// <summary>Returns true if the configured GitHub Project exists and is accessible on GitHub.</summary>
    Task<bool> ProjectExistsAsync(CancellationToken ct);

    /// <summary>Lists the configured repository's open pull requests (repo-centric; no project board needed).</summary>
    Task<IReadOnlyList<OpenPullRequest>> ListOpenPullRequestsAsync(CancellationToken ct);

    /// <summary>
    /// Returns the distinct head-commit SHAs that <paramref name="reviewerLogin"/> has already
    /// submitted a review against on the given PR. A reviewer skips a PR whose current head SHA is
    /// in this set (idempotency without local state).
    /// </summary>
    Task<IReadOnlyList<string>> GetReviewedHeadShasAsync(int pullRequestNumber, string reviewerLogin, CancellationToken ct);
}
