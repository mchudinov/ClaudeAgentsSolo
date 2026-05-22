using System.Text.Json;
using System.Text.Json.Serialization;
using DeveloperAgent.Configuration;
using Microsoft.Extensions.Options;
using Octokit;
using OctokitGraphQL = Octokit.GraphQL;

namespace DeveloperAgent.GitHub;

// ── Transport DTOs ────────────────────────────────────────────────────────────
// Internal because they are an implementation detail. Octokit types do not
// escape past GitHubProjectService; these DTOs are the boundary.

internal sealed record RestPullRequest(
    int Number,
    string HeadSha,
    string HtmlUrl,
    bool Merged);

internal sealed record RestPullRequestReview(
    long Id,
    string ReviewerLogin,
    string State,           // "APPROVED" | "CHANGES_REQUESTED" | "COMMENTED" | "DISMISSED"
    DateTimeOffset SubmittedAt);

internal sealed record RestPullRequestReviewComment(
    long Id,
    string UserLogin,
    string Body,
    DateTimeOffset CreatedAt,
    string? Path,
    int? Line);

internal sealed record RestIssueComment(
    long Id,
    string UserLogin,
    string Body,
    DateTimeOffset CreatedAt);

internal sealed record RestCheckRun(
    long Id,
    string Status,          // "queued" | "in_progress" | "completed"
    string? Conclusion);    // "success" | "failure" | "neutral" | "cancelled" | "timed_out" | "action_required" | "skipped" | "stale" | null

internal sealed record RestCreatePullRequestResult(
    bool AlreadyExists,
    RestPullRequest? PullRequest);

// ── Transport interfaces ──────────────────────────────────────────────────────

/// <summary>
/// Abstraction over GitHub's GraphQL API.
/// Real implementation: <see cref="OctokitGraphQLTransport"/>.
/// </summary>
internal interface IGraphQLTransport
{
    /// <summary>Executes a raw GraphQL query string and returns the parsed JSON element.</summary>
    Task<JsonElement> RunQueryAsync(string query, Dictionary<string, object>? variables, CancellationToken ct);

    /// <summary>Executes a raw GraphQL mutation string and returns the parsed JSON element.</summary>
    Task<JsonElement> RunMutationAsync(string mutation, Dictionary<string, object>? variables, CancellationToken ct);
}

/// <summary>
/// Abstraction over GitHub's REST API — semantic methods per call shape.
/// Real implementation: <see cref="OctokitRestTransport"/>.
/// </summary>
internal interface IRestTransport
{
    Task<RestPullRequest> GetPullRequestAsync(string owner, string repo, int number, CancellationToken ct);

    Task<IReadOnlyList<RestPullRequestReview>> GetPullRequestReviewsAsync(string owner, string repo, int number, CancellationToken ct);

    Task<IReadOnlyList<RestCheckRun>> GetCheckRunsAsync(string owner, string repo, string headSha, CancellationToken ct);

    /// <summary>
    /// Creates a pull request. Returns <see cref="RestCreatePullRequestResult.AlreadyExists"/> = true
    /// if a PR already exists for the head (422 already_exists from GitHub).
    /// </summary>
    Task<RestCreatePullRequestResult> CreatePullRequestAsync(string owner, string repo, string title, string body, string head, string baseBranch, CancellationToken ct);

    Task<IReadOnlyList<RestPullRequestReviewComment>> GetPullRequestReviewCommentsAsync(string owner, string repo, int number, CancellationToken ct);

    Task<IReadOnlyList<RestIssueComment>> GetIssueCommentsAsync(string owner, string repo, int number, CancellationToken ct);

    /// <summary>Finds the first open PR with the given head branch.</summary>
    Task<RestPullRequest?> FindOpenPullRequestByHeadAsync(string owner, string repo, string head, CancellationToken ct);

    /// <summary>Returns true if the repository exists and is accessible; false on 404.</summary>
    Task<bool> RepositoryExistsAsync(string owner, string repo, CancellationToken ct);
}

// ── Concrete implementations ──────────────────────────────────────────────────

/// <summary>
/// Wraps <see cref="OctokitGraphQL.Connection"/> to execute raw GraphQL queries.
/// Lazy-constructed: construction tolerates empty configuration.
/// </summary>
internal sealed class OctokitGraphQLTransport : IGraphQLTransport
{
    private readonly IOptions<GitHubOptions> _options;
    private readonly SecretsBundle _secrets;

    // Lazy so construction doesn't fail on missing config at startup
    private OctokitGraphQL.Connection? _connection;
    private readonly object _lock = new();

    public OctokitGraphQLTransport(IOptions<GitHubOptions> options, SecretsBundle secrets)
    {
        _options = options;
        _secrets = secrets;
    }

    private OctokitGraphQL.Connection GetConnection()
    {
        if (_connection is not null) return _connection;
        lock (_lock)
        {
            if (_connection is not null) return _connection;
            var version = typeof(OctokitGraphQLTransport).Assembly
                .GetName().Version?.ToString(3) ?? "1.0.0";
            _connection = new OctokitGraphQL.Connection(
                new OctokitGraphQL.ProductHeaderValue("DeveloperAgent", version),
                _secrets.GitHubToken);
            return _connection;
        }
    }

    public async Task<JsonElement> RunQueryAsync(string query, Dictionary<string, object>? variables, CancellationToken ct)
    {
        var json = await GetConnection().Run(BuildPayload(query, variables), ct).ConfigureAwait(false);
        return JsonDocument.Parse(json).RootElement;
    }

    public async Task<JsonElement> RunMutationAsync(string mutation, Dictionary<string, object>? variables, CancellationToken ct)
    {
        var json = await GetConnection().Run(BuildPayload(mutation, variables), ct).ConfigureAwait(false);
        return JsonDocument.Parse(json).RootElement;
    }

    private static string BuildPayload(string query, Dictionary<string, object>? variables)
    {
        if (variables is null or { Count: 0 })
            return JsonSerializer.Serialize(new { query });

        return JsonSerializer.Serialize(new { query, variables });
    }
}

/// <summary>
/// Wraps <see cref="GitHubClient"/> for REST-based GitHub operations.
/// Lazy-constructed: construction tolerates empty configuration.
/// </summary>
internal sealed class OctokitRestTransport : IRestTransport
{
    private readonly IOptions<GitHubOptions> _options;
    private readonly SecretsBundle _secrets;

    private GitHubClient? _client;
    private readonly object _lock = new();

    public OctokitRestTransport(IOptions<GitHubOptions> options, SecretsBundle secrets)
    {
        _options = options;
        _secrets = secrets;
    }

    private GitHubClient GetClient()
    {
        if (_client is not null) return _client;
        lock (_lock)
        {
            if (_client is not null) return _client;
            var version = typeof(OctokitRestTransport).Assembly
                .GetName().Version?.ToString(3) ?? "1.0.0";
            var client = new GitHubClient(new Octokit.ProductHeaderValue("DeveloperAgent", version))
            {
                Credentials = new Credentials(_secrets.GitHubToken)
            };
            _client = client;
            return _client;
        }
    }

    public async Task<RestPullRequest> GetPullRequestAsync(string owner, string repo, int number, CancellationToken ct)
    {
        var pr = await GetClient().PullRequest.Get(owner, repo, number).ConfigureAwait(false);
        return new RestPullRequest(pr.Number, pr.Head.Sha, pr.HtmlUrl, pr.Merged);
    }

    public async Task<IReadOnlyList<RestPullRequestReview>> GetPullRequestReviewsAsync(string owner, string repo, int number, CancellationToken ct)
    {
        var reviews = await GetClient().PullRequest.Review.GetAll(owner, repo, number).ConfigureAwait(false);
        return reviews
            .Select(r => new RestPullRequestReview(r.Id, r.User.Login, r.State.StringValue, r.SubmittedAt))
            .ToList();
    }

    public async Task<IReadOnlyList<RestCheckRun>> GetCheckRunsAsync(string owner, string repo, string headSha, CancellationToken ct)
    {
        var resp = await GetClient().Check.Run.GetAllForReference(owner, repo, headSha).ConfigureAwait(false);
        return resp.CheckRuns
            .Select(c => new RestCheckRun(
                c.Id,
                c.Status.StringValue,
                c.Conclusion.HasValue ? c.Conclusion.Value.StringValue : null))
            .ToList();
    }

    public async Task<RestCreatePullRequestResult> CreatePullRequestAsync(
        string owner, string repo, string title, string body,
        string head, string baseBranch, CancellationToken ct)
    {
        try
        {
            var newPr = new NewPullRequest(title, head, baseBranch) { Body = body };
            var pr = await GetClient().PullRequest.Create(owner, repo, newPr).ConfigureAwait(false);
            return new RestCreatePullRequestResult(false, new RestPullRequest(pr.Number, pr.Head.Sha, pr.HtmlUrl, pr.Merged));
        }
        catch (ApiValidationException ex)
            when (ex.ApiError?.Errors?.Any(e =>
                string.Equals(e.Code, "already_exists", StringComparison.OrdinalIgnoreCase)) == true)
        {
            return new RestCreatePullRequestResult(true, null);
        }
    }

    public async Task<IReadOnlyList<RestPullRequestReviewComment>> GetPullRequestReviewCommentsAsync(
        string owner, string repo, int number, CancellationToken ct)
    {
        var comments = await GetClient().PullRequest.ReviewComment.GetAll(owner, repo, number).ConfigureAwait(false);
        return comments
            .Select(c => new RestPullRequestReviewComment(c.Id, c.User.Login, c.Body, c.CreatedAt, c.Path, c.Position))
            .ToList();
    }

    public async Task<IReadOnlyList<RestIssueComment>> GetIssueCommentsAsync(
        string owner, string repo, int number, CancellationToken ct)
    {
        // Issue.Comment.GetAllForIssue takes issue number as long
        var comments = await GetClient().Issue.Comment.GetAllForIssue(owner, repo, (long)number).ConfigureAwait(false);
        return comments
            .Select(c => new RestIssueComment(c.Id, c.User.Login, c.Body, c.CreatedAt))
            .ToList();
    }

    public async Task<RestPullRequest?> FindOpenPullRequestByHeadAsync(
        string owner, string repo, string head, CancellationToken ct)
    {
        var request = new PullRequestRequest { Head = $"{owner}:{head}", State = ItemStateFilter.Open };
        var prs = await GetClient().PullRequest.GetAllForRepository(owner, repo, request).ConfigureAwait(false);
        var pr = prs.FirstOrDefault();
        if (pr is null) return null;
        return new RestPullRequest(pr.Number, pr.Head.Sha, pr.HtmlUrl, pr.Merged);
    }

    public async Task<bool> RepositoryExistsAsync(string owner, string repo, CancellationToken ct)
    {
        try
        {
            await GetClient().Repository.Get(owner, repo).ConfigureAwait(false);
            return true;
        }
        catch (NotFoundException)
        {
            return false;
        }
    }
}
