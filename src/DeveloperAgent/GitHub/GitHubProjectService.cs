using DeveloperAgent.Configuration;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.GitHub;

/// <summary>
/// Developer-agent facade over the agent-neutral <see cref="IGitHubProjectsClient"/>. Owns the
/// four-state lifecycle policy: it maps the <see cref="ProjectState"/> enum to/from the board's
/// status-column names (supplied by <see cref="ProjectStateNames"/>) so the rest of the agent keeps
/// working in typed lifecycle states while the client stays board-shape-agnostic. PR/comment/exists
/// operations are pure pass-throughs.
/// </summary>
internal sealed class GitHubProjectService : IGitHubProjectService
{
    private readonly IGitHubProjectsClient _client;
    private readonly ProjectStateNames _states;

    public GitHubProjectService(IGitHubProjectsClient client, IOptions<ProjectStateNames> states)
    {
        _client = client;
        _states = states.Value;
    }

    // ── State ↔ column-name mapping (the agent's lifecycle policy) ─────────────

    private string ToStatusName(ProjectState state) => state switch
    {
        ProjectState.Ready      => _states.Ready,
        ProjectState.InProgress => _states.InProgress,
        ProjectState.InReview   => _states.InReview,
        ProjectState.Done       => _states.Done,
        ProjectState.Backlog    => _states.Backlog,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    // Note the deliberate asymmetry with ToStatusName: ToState does NOT recognise the Backlog
    // column. Backlog is a write-only holding state — the agent moves items INTO it but must never
    // pick one OUT, so a Backlog item maps to no ProjectState and is dropped on read (see
    // ToProjectItem). The "never re-grabbed" guarantee is enforced here AND by the fact that the
    // pickup queries only ask for the Ready / InProgress / InReview columns.
    private ProjectState? ToState(string statusName)
    {
        if (string.Equals(statusName, _states.Ready, StringComparison.OrdinalIgnoreCase)) return ProjectState.Ready;
        if (string.Equals(statusName, _states.InProgress, StringComparison.OrdinalIgnoreCase)) return ProjectState.InProgress;
        if (string.Equals(statusName, _states.InReview, StringComparison.OrdinalIgnoreCase)) return ProjectState.InReview;
        if (string.Equals(statusName, _states.Done, StringComparison.OrdinalIgnoreCase)) return ProjectState.Done;
        return null;
    }

    /// <summary>
    /// Maps a board item onto a typed <see cref="ProjectItem"/>, or <see langword="null"/> when its
    /// status is not one of the known workable lifecycle states (e.g. Backlog, or any unrecognised
    /// column) — mirroring the old "unknown status excluded" behaviour and keeping parked items
    /// out of the agent's reach.
    /// </summary>
    private ProjectItem? ToProjectItem(ProjectBoardItem? item)
    {
        if (item is null) return null;
        var state = ToState(item.Status);
        if (state is null) return null;
        return new ProjectItem(
            item.ProjectItemId, item.ContentNodeId, item.ContentNumber,
            item.Title, item.BodyMarkdown, state.Value);
    }

    // ── IGitHubProjectService — board operations (typed) ───────────────────────

    public async Task<ProjectItem?> TryGetNextReadyItemAsync(CancellationToken ct)
        => ToProjectItem(await _client.TryGetNextItemInStatusAsync(_states.Ready, ct).ConfigureAwait(false));

    public Task MoveItemAsync(string projectItemId, ProjectState current, ProjectState target, CancellationToken ct)
        => _client.MoveItemAsync(projectItemId, ToStatusName(current), ToStatusName(target), ct);

    public async Task<IReadOnlyList<ProjectItem>> GetInFlightItemsAsync(CancellationToken ct)
    {
        var items = await _client
            .GetItemsInStatusesAsync([_states.InProgress, _states.InReview], ct)
            .ConfigureAwait(false);

        return items
            .Select(ToProjectItem)
            .Where(i => i is not null)
            .Select(i => i!)
            .ToList();
    }

    public Task<int> GetReadyItemCountAsync(CancellationToken ct)
        => _client.GetItemCountInStatusAsync(_states.Ready, ct);

    // ── IGitHubProjectService — pass-throughs (agent-neutral mechanics) ────────

    public Task AddItemCommentAsync(string contentNodeId, string markdownBody, CancellationToken ct)
        => _client.AddItemCommentAsync(contentNodeId, markdownBody, ct);

    public Task<PullRequest> CreatePullRequestAsync(CreatePullRequest request, CancellationToken ct)
        => _client.CreatePullRequestAsync(request, ct);

    public Task<PullRequestStatus> GetPullRequestStatusAsync(int pullRequestNumber, CancellationToken ct)
        => _client.GetPullRequestStatusAsync(pullRequestNumber, ct);

    public Task<PullRequestReviewContext> GetPullRequestForReviewAsync(int pullRequestNumber, CancellationToken ct)
        => _client.GetPullRequestForReviewAsync(pullRequestNumber, ct);

    public Task SubmitReviewAsync(int pullRequestNumber, ReviewVerdict verdict, string body, CancellationToken ct)
        => _client.SubmitReviewAsync(pullRequestNumber, verdict, body, ct);

    public Task<string> GetReviewFeedbackSinceAsync(int pullRequestNumber, DateTimeOffset sinceUtc, CancellationToken ct)
        => _client.GetReviewFeedbackSinceAsync(pullRequestNumber, sinceUtc, ct);

    public Task<bool> RepositoryExistsAsync(CancellationToken ct)
        => _client.RepositoryExistsAsync(ct);

    public Task<bool> ProjectExistsAsync(CancellationToken ct)
        => _client.ProjectExistsAsync(ct);
}
