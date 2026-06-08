using Agent.GitHub;
using Agent.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agent.Review;

/// <summary>
/// Reviews a pull request using Microsoft Agent Framework (the reviewer persona is the system
/// prompt) plus two deterministic pre-checks. See <see cref="IReviewerAgent"/> for the flow.
/// The chat client is built per review from <see cref="IAgentChatClientFactory"/> so tests can
/// substitute a scripted client; the persona is supplied via <c>ChatOptions.Instructions</c>.
/// </summary>
public sealed class ReviewerAgent : IReviewerAgent
{
    private const int MaxTokens = 8_000;

    private readonly IGitHubProjectsClient _gitHub;
    private readonly IAgentChatClientFactory _chatClientFactory;
    private readonly ReviewerPersonaLoader _persona;
    private readonly ReviewerOptions _reviewerOptions;
    private readonly ILogger<ReviewerAgent> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public ReviewerAgent(
        IGitHubProjectsClient gitHub,
        IAgentChatClientFactory chatClientFactory,
        ReviewerPersonaLoader persona,
        IOptions<ReviewerOptions> reviewerOptions,
        ILogger<ReviewerAgent> logger,
        ILoggerFactory? loggerFactory = null)
    {
        _gitHub = gitHub;
        _chatClientFactory = chatClientFactory;
        _persona = persona;
        _reviewerOptions = reviewerOptions.Value;
        _logger = logger;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public async Task<ReviewResult> ReviewAsync(int pullRequestNumber, CancellationToken ct)
    {
        var context = await _gitHub.GetPullRequestForReviewAsync(pullRequestNumber, ct).ConfigureAwait(false);

        // ── Deterministic check 1: required PR-body sections ───────────────────────
        var missing = MissingSections(context.Body);
        if (missing.Count > 0)
        {
            var summary =
                "RequestChanges — the PR body is missing required section(s): " +
                string.Join(", ", missing) +
                ". Every PR body must contain " +
                string.Join(", ", _reviewerOptions.RequiredPrBodySections) +
                ", in that order, with non-empty content under each.";
            _logger.LogInformation(
                "PR #{Number}: RequestChanges (missing sections: {Missing})",
                pullRequestNumber, string.Join(", ", missing));
            return await PostAsync(pullRequestNumber, ReviewVerdict.RequestChanges, summary, usedModel: false, ct)
                .ConfigureAwait(false);
        }

        // ── Deterministic check 2: oversized diff ─────────────────────────────────
        if (context.ChangedFiles > _reviewerOptions.MaxDiffFiles ||
            context.ChangedLines > _reviewerOptions.MaxDiffLines)
        {
            var summary =
                $"RequestChanges — the diff is too large to review safely: " +
                $"{context.ChangedFiles} changed file(s) (limit {_reviewerOptions.MaxDiffFiles}) and " +
                $"{context.ChangedLines} changed line(s) (limit {_reviewerOptions.MaxDiffLines}). " +
                "Split the change into smaller, independently reviewable PRs.";
            _logger.LogInformation(
                "PR #{Number}: RequestChanges (oversized diff: {Files} files, {Lines} lines)",
                pullRequestNumber, context.ChangedFiles, context.ChangedLines);
            return await PostAsync(pullRequestNumber, ReviewVerdict.RequestChanges, summary, usedModel: false, ct)
                .ConfigureAwait(false);
        }

        // ── Model-backed persona-violation scan ───────────────────────────────────
        var (verdict, scanSummary) = await RunPersonaScanAsync(context, ct).ConfigureAwait(false);
        return await PostAsync(pullRequestNumber, verdict, scanSummary, usedModel: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the required section headers absent from <paramref name="body"/>, in order. An empty
    /// <see cref="ReviewerOptions.RequiredPrBodySections"/> means the check is skipped (no missing).
    /// </summary>
    private List<string> MissingSections(string body)
        => MarkdownSectionBuilder
            .FindMissingSections(body, _reviewerOptions.RequiredPrBodySections)
            .ToList();

    private async Task<(ReviewVerdict Verdict, string Summary)> RunPersonaScanAsync(
        PullRequestReviewContext context, CancellationToken ct)
    {
        var submitTool = new SubmitReviewTool();
        var chatClient = _chatClientFactory.Create(_reviewerOptions.Model);

        var agentOptions = new ChatClientAgentOptions
        {
            Name = "ReviewerAgent",
            ChatOptions = new ChatOptions
            {
                Instructions = _persona.Persona,
                Tools = [submitTool],
                MaxOutputTokens = MaxTokens,
                // Temperature intentionally NOT set: newer Anthropic models reject a `temperature`
                // request field; leaving it null makes the provider omit it.
                AllowMultipleToolCalls = false,
            },
        };

        var agent = new ChatClientAgent(chatClient, agentOptions, _loggerFactory);
        var kickoff = new ChatMessage(ChatRole.User, BuildScanPrompt(context));

        try
        {
            var session = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
            await agent.RunAsync(kickoff, session, options: null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Persona scan failed for PR #{Number}; failing closed", context.Number);
            return (ReviewVerdict.RequestChanges,
                "RequestChanges — the reviewer could not complete its persona scan due to an " +
                $"internal error ({ex.GetType().Name}). Re-run the review once the issue is resolved.");
        }

        if (submitTool.RecordedVerdict is { } verdict)
            return (verdict, submitTool.RecordedSummary ?? string.Empty);

        _logger.LogWarning(
            "PR #{Number}: reviewer model did not call submit_review; failing closed", context.Number);
        return (ReviewVerdict.RequestChanges,
            "RequestChanges — the reviewer did not produce a verdict via submit_review. Please re-run the review.");
    }

    private async Task<ReviewResult> PostAsync(
        int pullRequestNumber, ReviewVerdict verdict, string summary, bool usedModel, CancellationToken ct)
    {
        await _gitHub.SubmitReviewAsync(pullRequestNumber, verdict, summary, ct).ConfigureAwait(false);
        return new ReviewResult(verdict, summary, usedModel);
    }

    private static string BuildScanPrompt(PullRequestReviewContext context)
        => $"Review pull request #{context.Number}.\n\n" +
           $"The PR body passed the deterministic section and diff-size pre-checks.\n" +
           $"Your job now is to scan the diff for violations (correctness, security, missing tests, " +
           $"nullable/async/DI issues, secrets in code or logs, unrelated churn). Read the whole diff " +
           $"before deciding.\n\n" +
           $"PR body:\n{context.Body}\n\n" +
           $"Unified diff ({context.ChangedFiles} files, {context.ChangedLines} lines):\n{context.UnifiedDiff}\n\n" +
           $"When done, call submit_review exactly once with your verdict and a markdown summary.";
}
