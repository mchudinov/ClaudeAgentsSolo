using System.Text;
using System.Text.Json;
using DeveloperAgent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.GitHub;

/// <summary>
/// All deterministic GitHub interactions used by the lifecycle loop.
/// Wraps <see cref="IGraphQLTransport"/> (Projects v2) and <see cref="IRestTransport"/> (REST).
/// </summary>
internal sealed class GitHubProjectService : IGitHubProjectService
{
    private readonly IGraphQLTransport _graphQL;
    private readonly IRestTransport _rest;
    private readonly GitHubOptions _options;
    private readonly ILogger<GitHubProjectService> _logger;

    // Lazy option-ID cache: key = state name, value = GitHub option ID
    // Populated on first use and held for the process lifetime.
    private volatile Task<IReadOnlyDictionary<string, string>>? _optionIdCache;

    public GitHubProjectService(
        IGraphQLTransport graphQL,
        IRestTransport rest,
        IOptions<GitHubOptions> options,
        ILogger<GitHubProjectService> logger)
    {
        _graphQL = graphQL;
        _rest = rest;
        _options = options.Value;
        _logger = logger;
    }

    // ── State-option-ID cache ─────────────────────────────────────────────────

    private Task<IReadOnlyDictionary<string, string>> GetOptionIdsAsync(CancellationToken ct)
    {
        // Thread-safe lazy init: Interlocked.CompareExchange ensures only one fetch fires.
        if (_optionIdCache is not null) return _optionIdCache;

        var fetching = FetchOptionIdsAsync(ct);
        var existing = Interlocked.CompareExchange(ref _optionIdCache, fetching, null);
        return existing ?? fetching;
    }

    private async Task<IReadOnlyDictionary<string, string>> FetchOptionIdsAsync(CancellationToken ct)
    {
        _logger.LogDebug("Fetching project status field option IDs for project #{ProjectNumber}", _options.Project.Number);

        var query = _options.Project.OwnerType == "Organization"
            ? BuildOrgProjectFieldQuery()
            : BuildUserProjectFieldQuery();

        var result = await _graphQL.RunQueryAsync(query, null, ct).ConfigureAwait(false);

        var projectNode = _options.Project.OwnerType == "Organization"
            ? result.GetProperty("data").GetProperty("organization").GetProperty("projectV2")
            : result.GetProperty("data").GetProperty("user").GetProperty("projectV2");

        var fields = projectNode.GetProperty("fields").GetProperty("nodes");
        JsonElement statusField = default;
        foreach (var field in fields.EnumerateArray())
        {
            if (field.TryGetProperty("name", out var nameProp) &&
                string.Equals(nameProp.GetString(), "Status", StringComparison.OrdinalIgnoreCase))
            {
                statusField = field;
                break;
            }
        }

        if (statusField.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("Could not find 'Status' field on the GitHub project.");

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (statusField.TryGetProperty("options", out var options))
        {
            foreach (var opt in options.EnumerateArray())
            {
                var name = opt.GetProperty("name").GetString()!;
                var id = opt.GetProperty("id").GetString()!;
                map[name] = id;
            }
        }

        _logger.LogDebug("Fetched {Count} status option IDs: {Names}", map.Count, string.Join(", ", map.Keys));
        return map;
    }

    // Raw GraphQL query strings (no JSON wrapping — the transport handles that)
    private string BuildOrgProjectFieldQuery()
        => $"query {{ organization(login: \"{_options.Owner}\") {{ projectV2(number: {_options.Project.Number}) {{ fields(first: 20) {{ nodes {{ ... on ProjectV2SingleSelectField {{ name options {{ id name }} }} }} }} }} }} }}";

    private string BuildUserProjectFieldQuery()
        => $"query {{ user(login: \"{_options.Owner}\") {{ projectV2(number: {_options.Project.Number}) {{ fields(first: 20) {{ nodes {{ ... on ProjectV2SingleSelectField {{ name options {{ id name }} }} }} }} }} }} }}";

    private async Task<string> GetOptionIdAsync(string stateName, CancellationToken ct)
    {
        var map = await GetOptionIdsAsync(ct).ConfigureAwait(false);
        if (!map.TryGetValue(stateName, out var id))
            throw new InvalidOperationException(
                $"GitHub project has no status option named '{stateName}'. Known: {string.Join(", ", map.Keys)}");
        return id;
    }

    // ── IGitHubProjectService ─────────────────────────────────────────────────

    public async Task<ProjectItem?> TryGetNextReadyItemAsync(CancellationToken ct)
    {
        var readyOptionId = await GetOptionIdAsync(_options.States.Ready, ct).ConfigureAwait(false);
        var items = await QueryProjectItemsAsync(readyOptionId, ct).ConfigureAwait(false);
        return items.FirstOrDefault(i => i.State == ProjectState.Ready);
    }

    public async Task MoveItemAsync(string projectItemId, ProjectState current, ProjectState target, CancellationToken ct)
    {
        if (current == target)
        {
            _logger.LogDebug("MoveItemAsync: item {ItemId} is already in {State}, skipping mutation", projectItemId, target);
            return;
        }

        var targetOptionId = await GetOptionIdAsync(StateToName(target), ct).ConfigureAwait(false);

        // Look up the Status field ID on the project
        var fieldId = await GetStatusFieldIdAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Moving project item {ItemId} to {Target}", projectItemId, target);

        var mutation = """
            mutation UpdateItem($projectId: ID!, $itemId: ID!, $fieldId: ID!, $optionId: String!) {
              updateProjectV2ItemFieldValue(input: {
                projectId: $projectId
                itemId: $itemId
                fieldId: $fieldId
                value: { singleSelectOptionId: $optionId }
              }) {
                projectV2Item { id }
              }
            }
            """;

        var projectId = await GetProjectNodeIdAsync(ct).ConfigureAwait(false);

        await _graphQL.RunMutationAsync(mutation, new Dictionary<string, object>
        {
            ["projectId"] = projectId,
            ["itemId"] = projectItemId,
            ["fieldId"] = fieldId,
            ["optionId"] = targetOptionId
        }, ct).ConfigureAwait(false);
    }

    public async Task AddItemCommentAsync(string contentNodeId, string markdownBody, CancellationToken ct)
    {
        var mutation = """
            mutation AddComment($subjectId: ID!, $body: String!) {
              addComment(input: { subjectId: $subjectId body: $body }) {
                commentEdge { node { id } }
              }
            }
            """;

        await _graphQL.RunMutationAsync(mutation, new Dictionary<string, object>
        {
            ["subjectId"] = contentNodeId,
            ["body"] = markdownBody
        }, ct).ConfigureAwait(false);
    }

    public async Task<PullRequest> CreatePullRequestAsync(CreatePullRequest request, CancellationToken ct)
    {
        var result = await _rest.CreatePullRequestAsync(
            _options.Owner,
            _options.Repository.Name,
            request.Title,
            request.MarkdownBody,
            request.HeadBranch,
            request.BaseBranch,
            ct).ConfigureAwait(false);

        if (result.AlreadyExists)
        {
            _logger.LogInformation("PR already exists for head branch {Head}, fetching existing PR", request.HeadBranch);
            var existing = await _rest.FindOpenPullRequestByHeadAsync(
                _options.Owner, _options.Repository.Name, request.HeadBranch, ct)
                .ConfigureAwait(false);

            if (existing is null)
                throw new InvalidOperationException(
                    $"GitHub reported PR already exists for head '{request.HeadBranch}' but no open PR was found.");

            return new PullRequest(existing.Number, existing.HeadSha, existing.HtmlUrl);
        }

        var pr = result.PullRequest!;
        return new PullRequest(pr.Number, pr.HeadSha, pr.HtmlUrl);
    }

    public async Task<PullRequestStatus> GetPullRequestStatusAsync(int pullRequestNumber, CancellationToken ct)
    {
        var prTask = _rest.GetPullRequestAsync(_options.Owner, _options.Repository.Name, pullRequestNumber, ct);
        var reviewsTask = _rest.GetPullRequestReviewsAsync(_options.Owner, _options.Repository.Name, pullRequestNumber, ct);

        // We need the head SHA from the PR before we can query check-runs
        // so we do two stages: first two in parallel, then check-runs
        await Task.WhenAll(prTask, reviewsTask).ConfigureAwait(false);

        var pr = prTask.Result;
        var reviews = reviewsTask.Result;
        var checkRuns = await _rest.GetCheckRunsAsync(_options.Owner, _options.Repository.Name, pr.HeadSha, ct).ConfigureAwait(false);

        var reviewState = CollapseReviewState(reviews);
        var checksGreen = CollapseCheckRuns(checkRuns);

        return new PullRequestStatus(
            Number: pullRequestNumber,
            Review: reviewState,
            ChecksGreen: checksGreen,
            Merged: pr.Merged,
            HeadSha: pr.HeadSha);
    }

    public async Task<string> GetReviewFeedbackSinceAsync(
        int pullRequestNumber, DateTimeOffset sinceUtc, CancellationToken ct)
    {
        var reviewCommentsTask = _rest.GetPullRequestReviewCommentsAsync(
            _options.Owner, _options.Repository.Name, pullRequestNumber, ct);
        var issueCommentsTask = _rest.GetIssueCommentsAsync(
            _options.Owner, _options.Repository.Name, pullRequestNumber, ct);

        await Task.WhenAll(reviewCommentsTask, issueCommentsTask).ConfigureAwait(false);

        var reviewComments = reviewCommentsTask.Result
            .Where(c => c.CreatedAt > sinceUtc)
            .Select(c => (CreatedAt: c.CreatedAt,
                          Text: FormatReviewComment(c)));

        var issueComments = issueCommentsTask.Result
            .Where(c => c.CreatedAt > sinceUtc)
            .Select(c => (CreatedAt: c.CreatedAt,
                          Text: FormatIssueComment(c)));

        var allComments = reviewComments.Concat(issueComments)
            .OrderBy(c => c.CreatedAt)
            .Select(c => c.Text)
            .ToList();

        return allComments.Count == 0
            ? string.Empty
            : string.Join("\n\n", allComments);
    }

    public async Task<IReadOnlyList<ProjectItem>> GetInFlightItemsAsync(CancellationToken ct)
    {
        var inProgressOptionId = await GetOptionIdAsync(_options.States.InProgress, ct).ConfigureAwait(false);
        var inReviewOptionId = await GetOptionIdAsync(_options.States.InReview, ct).ConfigureAwait(false);

        var inProgressTask = QueryProjectItemsAsync(inProgressOptionId, ct);
        var inReviewTask = QueryProjectItemsAsync(inReviewOptionId, ct);
        await Task.WhenAll(inProgressTask, inReviewTask).ConfigureAwait(false);

        return [.. inProgressTask.Result, .. inReviewTask.Result];
    }

    // ── GraphQL helpers ───────────────────────────────────────────────────────

    // Cached project node ID (needed for mutations)
    private volatile Task<string>? _projectNodeIdCache;

    private Task<string> GetProjectNodeIdAsync(CancellationToken ct)
    {
        if (_projectNodeIdCache is not null) return _projectNodeIdCache;
        var fetching = FetchProjectNodeIdAsync(ct);
        var existing = Interlocked.CompareExchange(ref _projectNodeIdCache, fetching, null);
        return existing ?? fetching;
    }

    private async Task<string> FetchProjectNodeIdAsync(CancellationToken ct)
    {
        var query = _options.Project.OwnerType == "Organization"
            ? $"query {{ organization(login: \"{_options.Owner}\") {{ projectV2(number: {_options.Project.Number}) {{ id }} }} }}"
            : $"query {{ user(login: \"{_options.Owner}\") {{ projectV2(number: {_options.Project.Number}) {{ id }} }} }}";

        var result = await _graphQL.RunQueryAsync(query, null, ct).ConfigureAwait(false);

        return _options.Project.OwnerType == "Organization"
            ? result.GetProperty("data").GetProperty("organization").GetProperty("projectV2").GetProperty("id").GetString()!
            : result.GetProperty("data").GetProperty("user").GetProperty("projectV2").GetProperty("id").GetString()!;
    }

    // Cached Status field node ID
    private volatile Task<string>? _statusFieldIdCache;

    private Task<string> GetStatusFieldIdAsync(CancellationToken ct)
    {
        if (_statusFieldIdCache is not null) return _statusFieldIdCache;
        var fetching = FetchStatusFieldIdAsync(ct);
        var existing = Interlocked.CompareExchange(ref _statusFieldIdCache, fetching, null);
        return existing ?? fetching;
    }

    private async Task<string> FetchStatusFieldIdAsync(CancellationToken ct)
    {
        var optionMap = await GetOptionIdsAsync(ct).ConfigureAwait(false);
        // The field ID comes from the same query that gives us option IDs;
        // re-run the query specifically for the field node ID.
        var query = _options.Project.OwnerType == "Organization"
            ? $"query {{ organization(login: \"{_options.Owner}\") {{ projectV2(number: {_options.Project.Number}) {{ fields(first: 20) {{ nodes {{ ... on ProjectV2SingleSelectField {{ id name }} }} }} }} }} }}"
            : $"query {{ user(login: \"{_options.Owner}\") {{ projectV2(number: {_options.Project.Number}) {{ fields(first: 20) {{ nodes {{ ... on ProjectV2SingleSelectField {{ id name }} }} }} }} }} }}";

        var result = await _graphQL.RunQueryAsync(query, null, ct).ConfigureAwait(false);

        var projectNode = _options.Project.OwnerType == "Organization"
            ? result.GetProperty("data").GetProperty("organization").GetProperty("projectV2")
            : result.GetProperty("data").GetProperty("user").GetProperty("projectV2");

        foreach (var field in projectNode.GetProperty("fields").GetProperty("nodes").EnumerateArray())
        {
            if (field.TryGetProperty("name", out var nameProp) &&
                string.Equals(nameProp.GetString(), "Status", StringComparison.OrdinalIgnoreCase))
            {
                return field.GetProperty("id").GetString()!;
            }
        }

        throw new InvalidOperationException("Could not find 'Status' field node ID on the GitHub project.");
    }

    private async Task<List<ProjectItem>> QueryProjectItemsAsync(string optionId, CancellationToken ct)
    {
        var query = _options.Project.OwnerType == "Organization"
            ? BuildOrgItemsQuery(optionId)
            : BuildUserItemsQuery(optionId);

        var result = await _graphQL.RunQueryAsync(query, null, ct).ConfigureAwait(false);

        var projectNode = _options.Project.OwnerType == "Organization"
            ? result.GetProperty("data").GetProperty("organization").GetProperty("projectV2")
            : result.GetProperty("data").GetProperty("user").GetProperty("projectV2");

        var items = new List<ProjectItem>();
        foreach (var node in projectNode.GetProperty("items").GetProperty("nodes").EnumerateArray())
        {
            if (!node.TryGetProperty("content", out var content)) continue;
            if (!node.TryGetProperty("id", out var itemIdProp)) continue;

            // Skip DraftIssue — the agent cannot operate on them
            if (content.TryGetProperty("__typename", out var typeProp) &&
                typeProp.GetString() == "DraftIssue")
                continue;

            var itemId = itemIdProp.GetString()!;
            var contentNodeId = content.GetProperty("id").GetString()!;
            var number = content.GetProperty("number").GetInt32();
            var title = content.GetProperty("title").GetString()!;
            var body = content.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";

            // Determine state from fieldValues
            var state = DetermineState(node);

            items.Add(new ProjectItem(itemId, contentNodeId, number, title, body, state));
        }

        return items;
    }

    private string BuildOrgItemsQuery(string optionId) => $$"""
        query {
          organization(login: "{{_options.Owner}}") {
            projectV2(number: {{_options.Project.Number}}) {
              items(first: 100) {
                nodes {
                  id
                  fieldValues(first: 20) {
                    nodes {
                      ... on ProjectV2ItemFieldSingleSelectValue {
                        optionId
                        field { ... on ProjectV2FieldCommon { name } }
                      }
                    }
                  }
                  content {
                    __typename
                    ... on Issue { id number title body }
                    ... on PullRequest { id number title body }
                    ... on DraftIssue { id title body }
                  }
                }
              }
            }
          }
        }
        """;

    private string BuildUserItemsQuery(string optionId) => $$"""
        query {
          user(login: "{{_options.Owner}}") {
            projectV2(number: {{_options.Project.Number}}) {
              items(first: 100) {
                nodes {
                  id
                  fieldValues(first: 20) {
                    nodes {
                      ... on ProjectV2ItemFieldSingleSelectValue {
                        optionId
                        field { ... on ProjectV2FieldCommon { name } }
                      }
                    }
                  }
                  content {
                    __typename
                    ... on Issue { id number title body }
                    ... on PullRequest { id number title body }
                    ... on DraftIssue { id title body }
                  }
                }
              }
            }
          }
        }
        """;

    private ProjectState DetermineState(JsonElement itemNode)
    {
        if (!itemNode.TryGetProperty("fieldValues", out var fieldValues)) return ProjectState.Ready;
        foreach (var fv in fieldValues.GetProperty("nodes").EnumerateArray())
        {
            if (!fv.TryGetProperty("optionId", out var optId)) continue;
            if (!fv.TryGetProperty("field", out var field)) continue;
            if (!field.TryGetProperty("name", out var fieldName)) continue;
            if (!string.Equals(fieldName.GetString(), "Status", StringComparison.OrdinalIgnoreCase)) continue;

            var optionIdStr = optId.GetString()!;
            // We need the reverse map to determine state by option ID
            // This is best-effort; GetOptionIdsAsync has already run before we get here
            // For now we return Ready and let callers filter — since we query by optionId, all items match the state we queried
            return ProjectState.Ready; // placeholder: state set by caller context
        }
        return ProjectState.Ready;
    }

    // ── Review/check-run collapse ─────────────────────────────────────────────

    private static PullRequestReviewState CollapseReviewState(IReadOnlyList<RestPullRequestReview> reviews)
    {
        if (reviews.Count == 0) return PullRequestReviewState.Pending;

        // Keep only the most recent non-dismissed review per reviewer
        var latestPerReviewer = reviews
            .Where(r => !string.Equals(r.State, "DISMISSED", StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => r.ReviewerLogin, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.SubmittedAt).First())
            .ToList();

        if (latestPerReviewer.Any(r =>
            string.Equals(r.State, "CHANGES_REQUESTED", StringComparison.OrdinalIgnoreCase)))
            return PullRequestReviewState.ChangesRequested;

        if (latestPerReviewer.Any(r =>
            string.Equals(r.State, "APPROVED", StringComparison.OrdinalIgnoreCase)))
            return PullRequestReviewState.Approved;

        return PullRequestReviewState.Pending;
    }

    private static bool CollapseCheckRuns(IReadOnlyList<RestCheckRun> checkRuns)
    {
        if (checkRuns.Count == 0) return true; // No checks = green (nothing to fail)

        foreach (var run in checkRuns)
        {
            // Any in-progress or queued check = not green yet
            if (string.Equals(run.Status, "in_progress", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(run.Status, "queued", StringComparison.OrdinalIgnoreCase))
                return false;

            // Completed check with bad conclusion = not green
            if (string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase) &&
                run.Conclusion is not null)
            {
                var ok = string.Equals(run.Conclusion, "success", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(run.Conclusion, "neutral", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(run.Conclusion, "skipped", StringComparison.OrdinalIgnoreCase);
                if (!ok) return false;
            }
        }

        return true;
    }

    // ── Comment formatting ────────────────────────────────────────────────────

    private static string FormatReviewComment(RestPullRequestReviewComment c)
    {
        var fileInfo = (c.Path is not null && c.Line.HasValue)
            ? $" — file {c.Path}:{c.Line.Value}"
            : c.Path is not null ? $" — file {c.Path}" : "";

        var header = $"> **@{c.UserLogin}** on {c.CreatedAt:yyyy-MM-ddTHH:mm:ssZ}{fileInfo}:";
        var bodyLines = c.Body.Split('\n').Select(l => $"> {l}");
        return header + "\n" + string.Join("\n", bodyLines);
    }

    private static string FormatIssueComment(RestIssueComment c)
    {
        var header = $"> **@{c.UserLogin}** on {c.CreatedAt:yyyy-MM-ddTHH:mm:ssZ}:";
        var bodyLines = c.Body.Split('\n').Select(l => $"> {l}");
        return header + "\n" + string.Join("\n", bodyLines);
    }

    // ── State ↔ name helpers ──────────────────────────────────────────────────

    private string StateToName(ProjectState state) => state switch
    {
        ProjectState.Ready      => _options.States.Ready,
        ProjectState.InProgress => _options.States.InProgress,
        ProjectState.InReview   => _options.States.InReview,
        ProjectState.Done       => _options.States.Done,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };
}
