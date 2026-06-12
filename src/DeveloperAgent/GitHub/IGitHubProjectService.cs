namespace DeveloperAgent.GitHub;

/// <summary>
/// All deterministic GitHub interactions used by the lifecycle loop.
/// The lifecycle loop never touches Octokit directly — it asks this service.
/// </summary>
public interface IGitHubProjectService
{
    /// <summary>
    /// Returns the next project item in the Ready state, or <see langword="null"/> if none.
    /// Selection order: by position in the Ready column (top first).
    /// DraftIssue content is skipped — the agent cannot operate on it.
    /// </summary>
    Task<ProjectItem?> TryGetNextReadyItemAsync(CancellationToken ct);

    /// <summary>
    /// Transitions a project item to a new state on the board.
    /// If <paramref name="current"/> == <paramref name="target"/>, this is a no-op (mutation is skipped).
    /// </summary>
    Task MoveItemAsync(string projectItemId, ProjectState current, ProjectState target, CancellationToken ct);

    /// <summary>
    /// Posts a markdown comment on the underlying issue node of the project item.
    /// </summary>
    Task AddItemCommentAsync(string contentNodeId, string markdownBody, CancellationToken ct);

    /// <summary>
    /// Fetches every comment on the item's underlying issue node, concatenated oldest-first as a
    /// single markdown blob (empty string when the item has no comments).
    /// </summary>
    Task<string> GetItemCommentsAsync(string contentNodeId, CancellationToken ct);

    /// <summary>
    /// Creates a pull request from <paramref name="request"/>.
    /// If a PR already exists for the head branch (422 already_exists),
    /// returns the existing PR instead of throwing.
    /// </summary>
    Task<PullRequest> CreatePullRequestAsync(CreatePullRequest request, CancellationToken ct);

    /// <summary>
    /// Returns a combined status snapshot for the pull request:
    /// merge status, aggregated review verdict, and check-run health.
    /// </summary>
    Task<PullRequestStatus> GetPullRequestStatusAsync(int pullRequestNumber, CancellationToken ct);

    /// <summary>
    /// Fetches everything the reviewer agent needs to review a pull request in one round-trip:
    /// the PR body, the changed-file/line counts, and the concatenated unified diff.
    /// </summary>
    Task<PullRequestReviewContext> GetPullRequestForReviewAsync(int pullRequestNumber, CancellationToken ct);

    /// <summary>
    /// Submits a review on the pull request with the given <paramref name="verdict"/> and
    /// markdown <paramref name="body"/>. <see cref="ReviewVerdict.Approve"/> maps to GitHub's
    /// APPROVE event; <see cref="ReviewVerdict.RequestChanges"/> maps to REQUEST_CHANGES.
    /// Never merges.
    /// </summary>
    Task SubmitReviewAsync(int pullRequestNumber, ReviewVerdict verdict, string body, CancellationToken ct);

    /// <summary>
    /// Fetches review-thread comments and issue comments newer than <paramref name="sinceUtc"/>
    /// on the pull request, concatenated as a single markdown blob.
    /// Returns an empty string if no comments are newer than the cursor.
    /// </summary>
    Task<string> GetReviewFeedbackSinceAsync(int pullRequestNumber, DateTimeOffset sinceUtc, CancellationToken ct);

    /// <summary>
    /// Returns items currently in the In Progress or In Review states.
    /// Used once at startup so the lifecycle loop can log and skip them.
    /// </summary>
    Task<IReadOnlyList<ProjectItem>> GetInFlightItemsAsync(CancellationToken ct);

    /// <summary>
    /// Returns the total number of items currently in the Ready state.
    /// </summary>
    Task<int> GetReadyItemCountAsync(CancellationToken ct);

    /// <summary>
    /// Returns true if the configured repository exists and is accessible on GitHub.
    /// </summary>
    Task<bool> RepositoryExistsAsync(CancellationToken ct);

    /// <summary>
    /// Returns true if the configured GitHub Project exists and is accessible on GitHub.
    /// </summary>
    Task<bool> ProjectExistsAsync(CancellationToken ct);

    /// <summary>
    /// Squash-merges the pull request (the developer-agent's chosen merge method). Idempotent:
    /// an already-merged PR returns <see cref="MergeOutcome.AlreadyMerged"/>.
    /// </summary>
    Task<MergeOutcome> SquashMergePullRequestAsync(int pullRequestNumber, CancellationToken ct);

    /// <summary>Deletes the head branch. A missing branch is a no-op.</summary>
    Task DeleteBranchAsync(string branchName, CancellationToken ct);
}
