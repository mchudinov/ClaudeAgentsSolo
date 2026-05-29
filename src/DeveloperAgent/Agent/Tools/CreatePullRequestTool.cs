using System.Text.Json;
using System.Text.Json.Nodes;
using DeveloperAgent.Configuration;
using DeveloperAgent.GitHub;
using DeveloperAgent.Workspace;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Agent.Tools;

/// <summary>
/// Opens a pull request from the current workspace branch into the default branch.
/// Builds the body from the four persona §9 sections and derives a title.
/// Stores the resulting <see cref="PullRequest"/> on the session for the runner to return.
/// </summary>
/// <remarks>
/// Step-21 (P2-H) adds the composite <c>MaxPRSize</c> gate: before opening the PR the tool
/// computes <c>git diff --numstat</c> stats and, if the changed-file or changed-line count
/// exceeds the configured cap, posts an explanatory comment to the item and returns an error
/// without opening the PR (block PR opening + comment + halt).
/// </remarks>
public sealed class CreatePullRequestTool : ITool
{
    private readonly IGitHubProjectService _github;
    private readonly IGitClient _gitClient;
    private readonly ScopeLimitOptions _scopeLimits;

    /// <summary>Initialises the tool with the GitHub service, git client, and scope limits.</summary>
    public CreatePullRequestTool(
        IGitHubProjectService github,
        IGitClient gitClient,
        IOptions<ScopeLimitOptions> scopeLimits)
    {
        _github = github;
        _gitClient = gitClient;
        _scopeLimits = scopeLimits.Value;
    }

    public string Name => "create_pull_request";
    public string Description => "Open a pull request from the current branch into the default branch using the four-section PR body from persona §9.";

    public JsonNode InputSchema { get; } = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "summary":               { "type": "string", "description": "What the PR changes and why. Required; must not be empty." },
            "user_visible_behavior": { "type": "string", "description": "What an external caller now sees. Use 'No user-visible behavior change' for pure refactors." },
            "tests_validation_run":  { "type": "string", "description": "Which tests ran and their results." },
            "notes_assumptions":     { "type": "string", "description": "Anything the reviewer needs to know." },
            "title":                 { "type": "string", "description": "Optional PR title override (≤72 chars). If omitted, derived from the first sentence of summary." }
          },
          "required": ["summary"]
        }
        """)!;

    public async Task<ToolResult> InvokeAsync(JsonNode input, ToolContext context, CancellationToken ct)
    {
        string? summary, userVisible, testsRun, notes, titleOverride;
        try
        {
            summary = input["summary"]?.GetValue<string>();
            userVisible = input["user_visible_behavior"]?.GetValue<string>() ?? string.Empty;
            testsRun = input["tests_validation_run"]?.GetValue<string>() ?? string.Empty;
            notes = input["notes_assumptions"]?.GetValue<string>() ?? string.Empty;
            titleOverride = input["title"]?.GetValue<string>();
        }
        catch (Exception ex)
        {
            return new ToolResult(true, $"Invalid input: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(summary))
            return new ToolResult(true, "Invalid input: 'summary' is required and must not be empty.");

        string body;
        try
        {
            body = PullRequestBodyBuilder.Build(summary, userVisible, testsRun, notes);
        }
        catch (ArgumentException ex)
        {
            return new ToolResult(true, $"Failed to build PR body: {ex.Message}");
        }

        // Derive title: override if provided; otherwise first sentence of summary ≤72 chars
        string title;
        if (!string.IsNullOrWhiteSpace(titleOverride))
        {
            title = titleOverride.Length > 72
                ? titleOverride[..72]
                : titleOverride;
        }
        else
        {
            // First non-empty sentence up to the first period, question mark, or 72 chars
            int sentenceEnd = summary.IndexOfAny(['.', '!', '?']);
            string sentence = sentenceEnd >= 0
                ? summary[..(sentenceEnd + 1)].Trim()
                : summary.Trim();
            title = sentence.Length > 72 ? sentence[..72] : sentence;
        }

        // ── Composite MaxPRSize gate (LLD §P2-H) ──────────────────────────────
        // Block PR opening when the changed-file OR changed-line count exceeds the cap.
        // On breach: post an explanatory comment to the item and halt (return error).
        DiffStats stats;
        try
        {
            stats = await _gitClient.GetDiffStatsAsync(
                context.Workspace, context.Workspace.DefaultBranch, ct);
        }
        catch (Exception ex)
        {
            return new ToolResult(true, $"Failed to compute PR size: {ex.Message}");
        }

        if (stats.ChangedFiles > _scopeLimits.MaxPRChangedFiles ||
            stats.ChangedLines > _scopeLimits.MaxPRChangedLines)
        {
            var comment =
                $"Pull request blocked: change set exceeds the configured MaxPRSize scope limit. " +
                $"Changed files: {stats.ChangedFiles} (cap {_scopeLimits.MaxPRChangedFiles}); " +
                $"changed lines: {stats.ChangedLines} (cap {_scopeLimits.MaxPRChangedLines}). " +
                $"Split the work into smaller pull requests.";

            try
            {
                await _github.AddItemCommentAsync(context.Item.ContentNodeId, comment, ct);
            }
            catch (Exception ex)
            {
                return new ToolResult(true,
                    $"PR blocked by MaxPRSize ({stats.ChangedFiles} files, {stats.ChangedLines} lines); " +
                    $"also failed to post explanatory comment: {ex.Message}");
            }

            return new ToolResult(true, comment);
        }

        var request = new CreatePullRequest(
            HeadBranch: context.Workspace.BranchName,
            BaseBranch: context.Workspace.DefaultBranch,
            Title: title,
            MarkdownBody: body);

        try
        {
            var pr = await _github.CreatePullRequestAsync(request, ct);
            context.Session.CreatedPullRequest = pr;
            var json = JsonSerializer.Serialize(new { number = pr.Number, html_url = pr.HtmlUrl, head_sha = pr.HeadSha });
            return new ToolResult(false, json);
        }
        catch (Exception ex)
        {
            return new ToolResult(true, $"Failed to create pull request: {ex.Message}");
        }
    }
}
