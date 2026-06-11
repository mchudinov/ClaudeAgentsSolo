using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.GitHub;

/// <summary>
/// Agent-neutral implementation of <see cref="IGitHubProjectsClient"/>: all deterministic GitHub
/// Projects v2 + pull-request interactions, keyed by status-column name. Wraps
/// <see cref="IGraphQLTransport"/> (Projects v2) and <see cref="IRestTransport"/> (REST).
/// Carries no opinion about which columns exist or how they sequence — that lifecycle policy lives
/// in the consuming app's facade.
/// </summary>
internal sealed class GitHubProjectsClient : IGitHubProjectsClient
{
    private readonly IGraphQLTransport _graphQL;
    private readonly IRestTransport _rest;
    private readonly GitHubOptions _options;
    private readonly ILogger<GitHubProjectsClient> _logger;

    /// <summary>
    /// Consolidated project metadata fetched in a single GraphQL round-trip.
    /// Held for the process lifetime after first use.
    /// </summary>
    private sealed record OptionLookup(
        /// <summary>Maps canonical status name (case-insensitive) → GitHub option ID.</summary>
        IReadOnlyDictionary<string, string> NameToId,
        /// <summary>Maps GitHub option ID → status name. Unknown IDs are absent.</summary>
        IReadOnlyDictionary<string, string> IdToName,
        /// <summary>GraphQL node ID of the Status field (needed for mutations).</summary>
        string StatusFieldNodeId);

    private volatile Task<OptionLookup>? _optionCache;

    public GitHubProjectsClient(
        IGraphQLTransport graphQL,
        IRestTransport rest,
        IOptions<GitHubOptions> options,
        ILogger<GitHubProjectsClient> logger)
    {
        _graphQL = graphQL;
        _rest = rest;
        _options = options.Value;
        _logger = logger;
    }

    // ── Config guard ──────────────────────────────────────────────────────────

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.Owner))
            throw new GitHubNotConfiguredException(
                "GitHub.Owner is empty. Set it in appsettings.Development.json (or via env var) to the org/user that owns the project. " +
                "Until configured, the poll loop will idle.");

        if (_options.Project.Number <= 0)
            throw new GitHubNotConfiguredException(
                $"GitHub.Project.Number is {_options.Project.Number}. Set it in appsettings.Development.json (or via env var) " +
                "to the project number shown in the GitHub UI URL. Until configured, the poll loop will idle.");
    }

    // ── Unified option/field cache ────────────────────────────────────────────

    private Task<OptionLookup> GetOptionLookupAsync(CancellationToken ct)
    {
        EnsureConfigured();
        if (_optionCache is not null) return _optionCache;
        var fetching = FetchOptionLookupAsync(ct);
        var existing = Interlocked.CompareExchange(ref _optionCache, fetching, null);
        return existing ?? fetching;
    }

    private async Task<OptionLookup> FetchOptionLookupAsync(CancellationToken ct)
    {
        _logger.LogDebug("Fetching project status field metadata for project #{ProjectNumber}", _options.Project.Number);

        // Single query: fetch field ID + all option IDs in one round-trip.
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

        var statusFieldNodeId = statusField.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Status field node ID is null.");

        // Build both directions of the name/id map from the options array in a single pass.
        var nameToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var idToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (statusField.TryGetProperty("options", out var optionsElement))
        {
            foreach (var opt in optionsElement.EnumerateArray())
            {
                var name = opt.GetProperty("name").GetString()!;
                var id = opt.GetProperty("id").GetString()!;
                nameToId[name] = id;
                idToName[id] = name;
            }
        }

        _logger.LogDebug("Fetched {Count} status options", nameToId.Count);

        return new OptionLookup(nameToId, idToName, statusFieldNodeId);
    }

    // Raw GraphQL query strings — include both `id` and `options` so a single
    // call covers field-node-ID + option-ID + name map in one round-trip.
    private string BuildOrgProjectFieldQuery()
        => $"query {{ organization(login: \"{_options.Owner}\") {{ projectV2(number: {_options.Project.Number}) {{ fields(first: 20) {{ nodes {{ ... on ProjectV2SingleSelectField {{ id name options {{ id name }} }} }} }} }} }} }}";

    private string BuildUserProjectFieldQuery()
        => $"query {{ user(login: \"{_options.Owner}\") {{ projectV2(number: {_options.Project.Number}) {{ fields(first: 20) {{ nodes {{ ... on ProjectV2SingleSelectField {{ id name options {{ id name }} }} }} }} }} }} }}";

    private async Task<string> GetOptionIdAsync(string statusName, CancellationToken ct)
    {
        var lookup = await GetOptionLookupAsync(ct).ConfigureAwait(false);
        if (!lookup.NameToId.TryGetValue(statusName, out var id))
            throw new InvalidOperationException(
                $"GitHub project has no status option named '{statusName}'. Known: {string.Join(", ", lookup.NameToId.Keys)}");
        return id;
    }

    // ── IGitHubProjectsClient ─────────────────────────────────────────────────

    public async Task<ProjectBoardItem?> TryGetNextItemInStatusAsync(string statusName, CancellationToken ct)
    {
        var optionId = await GetOptionIdAsync(statusName, ct).ConfigureAwait(false);
        var items = await QueryProjectItemsAsync(optionId, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Polling project #{ProjectNumber} ({Owner}): {Count} item(s) in status '{Status}'.",
            _options.Project.Number, _options.Owner, items.Count, statusName);

        return items.FirstOrDefault();
    }

    public async Task MoveItemAsync(string projectItemId, string fromStatusName, string toStatusName, CancellationToken ct)
    {
        if (string.Equals(fromStatusName, toStatusName, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("MoveItemAsync: item {ItemId} is already in '{Status}', skipping mutation", projectItemId, toStatusName);
            return;
        }

        var lookup = await GetOptionLookupAsync(ct).ConfigureAwait(false);
        if (!lookup.NameToId.TryGetValue(toStatusName, out var targetOptionId))
            throw new InvalidOperationException(
                $"GitHub project has no status option named '{toStatusName}'. Known: {string.Join(", ", lookup.NameToId.Keys)}");

        var fieldId = lookup.StatusFieldNodeId;

        _logger.LogInformation("Moving project item {ItemId} to '{Target}'", projectItemId, toStatusName);

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

    public async Task<MergeOutcome> MergePullRequestAsync(int pullRequestNumber, MergeMethod method, CancellationToken ct)
    {
        // Idempotency: a retried or replayed activity can run against a PR that is already merged.
        // Octokit throws if you merge an already-merged PR, so check first and short-circuit.
        var pr = await _rest.GetPullRequestAsync(_options.Owner, _options.Repository.Name, pullRequestNumber, ct)
            .ConfigureAwait(false);
        if (pr.Merged)
        {
            _logger.LogInformation("PR #{Number} already merged; treating merge as a no-op.", pullRequestNumber);
            return MergeOutcome.AlreadyMerged;
        }

        _logger.LogInformation("Merging PR #{Number} via {Method}.", pullRequestNumber, method);
        return await _rest.MergePullRequestAsync(
            _options.Owner, _options.Repository.Name, pullRequestNumber, method, ct).ConfigureAwait(false);
    }

    public Task DeleteBranchAsync(string branchName, CancellationToken ct)
        => _rest.DeleteBranchAsync(_options.Owner, _options.Repository.Name, branchName, ct);

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
            HeadSha: pr.HeadSha,
            Mergeable: pr.Mergeable,
            Closed: pr.Closed);
    }

    public async Task<PullRequestReviewContext> GetPullRequestForReviewAsync(int pullRequestNumber, CancellationToken ct)
    {
        var bodyTask = _rest.GetPullRequestBodyAsync(_options.Owner, _options.Repository.Name, pullRequestNumber, ct);
        var filesTask = _rest.GetPullRequestFilesAsync(_options.Owner, _options.Repository.Name, pullRequestNumber, ct);
        await Task.WhenAll(bodyTask, filesTask).ConfigureAwait(false);

        var body = bodyTask.Result;
        var files = filesTask.Result;

        var changedFiles = files.Count;
        var changedLines = files.Sum(f => f.Additions + f.Deletions);

        // Concatenate per-file patches into a single unified diff blob, each prefixed with a
        // git-style `diff --git` header so the model can attribute hunks to files. Files with
        // no patch (e.g. binary or pure renames) contribute only the header.
        var diff = string.Join("\n", files.Select(f =>
            $"diff --git a/{f.FileName} b/{f.FileName}\n{f.Patch ?? "(no textual diff)"}"));

        return new PullRequestReviewContext(pullRequestNumber, body, changedFiles, changedLines, diff);
    }

    public async Task SubmitReviewAsync(int pullRequestNumber, ReviewVerdict verdict, string body, CancellationToken ct)
    {
        _logger.LogInformation(
            "Submitting {Verdict} review on PR #{Number}", verdict, pullRequestNumber);
        await _rest.SubmitReviewAsync(
            _options.Owner, _options.Repository.Name, pullRequestNumber, verdict, body, ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OpenPullRequest>> ListOpenPullRequestsAsync(CancellationToken ct)
    {
        var prs = await _rest.ListOpenPullRequestsAsync(_options.Owner, _options.Repository.Name, ct)
            .ConfigureAwait(false);
        return prs
            .Select(p => new OpenPullRequest(p.Number, p.HeadSha, p.IsDraft, p.Author, p.HtmlUrl))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> GetReviewedHeadShasAsync(
        int pullRequestNumber, string reviewerLogin, CancellationToken ct)
    {
        var reviews = await _rest.GetPullRequestReviewsAsync(
            _options.Owner, _options.Repository.Name, pullRequestNumber, ct).ConfigureAwait(false);
        return reviews
            .Where(r => string.Equals(r.ReviewerLogin, reviewerLogin, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.CommitId)
            .Where(sha => !string.IsNullOrEmpty(sha))
            .Distinct(StringComparer.Ordinal)
            .ToList();
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

    public async Task<bool> RepositoryExistsAsync(CancellationToken ct)
    {
        EnsureConfigured();
        return await _rest.RepositoryExistsAsync(_options.Owner, _options.Repository.Name, ct).ConfigureAwait(false);
    }

    public async Task<bool> ProjectExistsAsync(CancellationToken ct)
    {
        EnsureConfigured();
        var query = _options.Project.OwnerType == "Organization"
            ? $"query {{ organization(login: \"{_options.Owner}\") {{ projectV2(number: {_options.Project.Number}) {{ id }} }} }}"
            : $"query {{ user(login: \"{_options.Owner}\") {{ projectV2(number: {_options.Project.Number}) {{ id }} }} }}";

        var result = await _graphQL.RunQueryAsync(query, null, ct).ConfigureAwait(false);

        var projectNode = _options.Project.OwnerType == "Organization"
            ? result.GetProperty("data").GetProperty("organization").GetProperty("projectV2")
            : result.GetProperty("data").GetProperty("user").GetProperty("projectV2");

        return projectNode.ValueKind != JsonValueKind.Null;
    }

    public async Task<int> GetItemCountInStatusAsync(string statusName, CancellationToken ct)
    {
        var optionId = await GetOptionIdAsync(statusName, ct).ConfigureAwait(false);
        var items = await QueryProjectItemsAsync(optionId, ct).ConfigureAwait(false);
        return items.Count;
    }

    public async Task<IReadOnlyList<ProjectBoardItem>> GetItemsInStatusesAsync(
        IReadOnlyCollection<string> statusNames, CancellationToken ct)
    {
        var optionIdTasks = statusNames.Select(name => GetOptionIdAsync(name, ct)).ToList();
        var optionIds = await Task.WhenAll(optionIdTasks).ConfigureAwait(false);

        var queryTasks = optionIds.Select(id => QueryProjectItemsAsync(id, ct)).ToList();
        var results = await Task.WhenAll(queryTasks).ConfigureAwait(false);

        return results.SelectMany(r => r).ToList();
    }

    // ── GraphQL helpers ───────────────────────────────────────────────────────

    // Cached project node ID (needed for mutations; kept separate from OptionLookup
    // because it comes from a different query path)
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

    private async Task<List<ProjectBoardItem>> QueryProjectItemsAsync(string optionId, CancellationToken ct)
    {
        var lookup = await GetOptionLookupAsync(ct).ConfigureAwait(false);

        var query = _options.Project.OwnerType == "Organization"
            ? BuildOrgItemsQuery()
            : BuildUserItemsQuery();

        var result = await _graphQL.RunQueryAsync(query, null, ct).ConfigureAwait(false);

        var projectNode = _options.Project.OwnerType == "Organization"
            ? result.GetProperty("data").GetProperty("organization").GetProperty("projectV2")
            : result.GetProperty("data").GetProperty("user").GetProperty("projectV2");

        // All returned items share the requested optionId, so their status is its name.
        var statusName = lookup.IdToName.TryGetValue(optionId, out var n) ? n : string.Empty;

        var items = new List<ProjectBoardItem>();
        foreach (var node in projectNode.GetProperty("items").GetProperty("nodes").EnumerateArray())
        {
            if (!node.TryGetProperty("content", out var content)) continue;
            if (!node.TryGetProperty("id", out var itemIdProp)) continue;

            // Filter to the requested optionId — only include items in the requested status.
            // Items in other columns (or with no status / an unknown option) are excluded.
            if (!optionId.Equals(GetOptionIdFromFieldValues(node), StringComparison.OrdinalIgnoreCase))
                continue;

            var itemId = itemIdProp.GetString()!;
            var contentNodeId = content.GetProperty("id").GetString()!;
            // DraftIssue content has no `number` field; default to 0 for drafts.
            var number = content.TryGetProperty("number", out var numProp) ? numProp.GetInt32() : 0;
            var title = content.GetProperty("title").GetString()!;
            var body = content.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";

            items.Add(new ProjectBoardItem(itemId, contentNodeId, number, title, body, statusName));
        }

        return items;
    }

    /// <summary>
    /// Extracts the raw option ID string from fieldValues.nodes for the Status field.
    /// Returns null if absent.
    /// </summary>
    private static string? GetOptionIdFromFieldValues(JsonElement itemNode)
    {
        if (!itemNode.TryGetProperty("fieldValues", out var fieldValues)) return null;
        foreach (var fv in fieldValues.GetProperty("nodes").EnumerateArray())
        {
            if (!fv.TryGetProperty("optionId", out var optId)) continue;
            if (!fv.TryGetProperty("field", out var field)) continue;
            if (!field.TryGetProperty("name", out var fieldName)) continue;
            if (!string.Equals(fieldName.GetString(), "Status", StringComparison.OrdinalIgnoreCase)) continue;
            return optId.GetString();
        }
        return null;
    }

    private string BuildOrgItemsQuery() => $$"""
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

    private string BuildUserItemsQuery() => $$"""
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
}
