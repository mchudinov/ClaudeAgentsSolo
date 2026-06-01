using DeveloperAgent.Configuration;
using DeveloperAgent.GitHub;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Agent.Review;

/// <summary>
/// Reviews a pull request using Microsoft Agent Framework (the reviewer persona is the system
/// prompt) plus two deterministic pre-checks. See <see cref="IReviewerAgent"/> for the flow.
/// </summary>
/// <remarks>
/// Construction mirrors <see cref="AnthropicAgentRunner"/>: the chat client is built per review
/// from <see cref="IAgentChatClientFactory"/> so unit tests can substitute a scripted client,
/// and the persona is supplied via <c>ChatOptions.Instructions</c> (not a user message).
/// </remarks>
public sealed class ReviewerAgent : IReviewerAgent
{
    private const int MaxTokens = 8_000;

    private readonly IGitHubProjectService _gitHub;
    private readonly IAgentChatClientFactory _chatClientFactory;
    private readonly ReviewerPersonaLoader _persona;
    private readonly AgentOptions _agentOptions;
    private readonly ReviewerOptions _reviewerOptions;
    private readonly ILogger<ReviewerAgent> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public ReviewerAgent(
        IGitHubProjectService gitHub,
        IAgentChatClientFactory chatClientFactory,
        ReviewerPersonaLoader persona,
        IOptions<AgentOptions> agentOptions,
        IOptions<ReviewerOptions> reviewerOptions,
        ILogger<ReviewerAgent> logger,
        ILoggerFactory? loggerFactory = null)
    {
        _gitHub = gitHub;
        _chatClientFactory = chatClientFactory;
        _persona = persona;
        _agentOptions = agentOptions.Value;
        _reviewerOptions = reviewerOptions.Value;
        _logger = logger;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public async Task<ReviewResult> ReviewAsync(int pullRequestNumber, CancellationToken ct)
    {
        var context = await _gitHub.GetPullRequestForReviewAsync(pullRequestNumber, ct).ConfigureAwait(false);

        // ── Deterministic check 1: four-section PR body ───────────────────────────
        var missing = MissingSections(context.Body);
        if (missing.Count > 0)
        {
            var summary =
                "RequestChanges — the PR body is missing required section(s): " +
                string.Join(", ", missing) +
                ". Per the developer persona §9, every PR body must contain " +
                string.Join(", ", PullRequestBodyBuilder.RequiredSectionHeaders) +
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
    /// Returns the canonical section headers absent from <paramref name="body"/>, preserving
    /// the required order. An empty list means every required section is present. Section
    /// knowledge comes from <see cref="PullRequestBodyBuilder.RequiredSectionHeaders"/>.
    /// </summary>
    private static List<string> MissingSections(string body)
    {
        var present = body ?? string.Empty;
        return PullRequestBodyBuilder.RequiredSectionHeaders
            .Where(header => !present.Contains(header, StringComparison.Ordinal))
            .ToList();
    }

    private async Task<(ReviewVerdict Verdict, string Summary)> RunPersonaScanAsync(
        PullRequestReviewContext context, CancellationToken ct)
    {
        var submitTool = new SubmitReviewTool();

        var chatClient = _chatClientFactory.Create(_agentOptions.Model);

        var agentOptions = new ChatClientAgentOptions
        {
            Name = "ReviewerAgent",
            ChatOptions = new ChatOptions
            {
                Instructions = _persona.Persona,
                Tools = [submitTool],
                MaxOutputTokens = MaxTokens,
                // NOTE: Temperature is intentionally NOT set. Newer Anthropic models reject a
                // `temperature` request field ("temperature is deprecated for this model" →
                // AnthropicBadRequestException); leaving it null makes the provider omit it.
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
            // A scan failure must not silently approve. Fail closed: request changes so a
            // human/the developer is alerted rather than merging an unreviewed PR.
            _logger.LogError(ex, "Persona scan failed for PR #{Number}; failing closed", context.Number);
            return (ReviewVerdict.RequestChanges,
                "RequestChanges — the reviewer could not complete its persona scan due to an " +
                $"internal error ({ex.GetType().Name}). Re-run the review once the issue is resolved.");
        }

        if (submitTool.RecordedVerdict is { } verdict)
            return (verdict, submitTool.RecordedSummary ?? string.Empty);

        // The model finished without calling submit_review. Fail closed rather than guessing.
        _logger.LogWarning(
            "PR #{Number}: reviewer model did not call submit_review; failing closed", context.Number);
        return (ReviewVerdict.RequestChanges,
            "RequestChanges — the reviewer did not produce a verdict via submit_review. " +
            "Please re-run the review.");
    }

    private async Task<ReviewResult> PostAsync(
        int pullRequestNumber, ReviewVerdict verdict, string summary, bool usedModel, CancellationToken ct)
    {
        await _gitHub.SubmitReviewAsync(pullRequestNumber, verdict, summary, ct).ConfigureAwait(false);
        return new ReviewResult(verdict, summary, usedModel);
    }

    private static string BuildScanPrompt(PullRequestReviewContext context)
        => $"Review pull request #{context.Number}.\n\n" +
           $"The PR body passed the deterministic four-section and diff-size pre-checks.\n" +
           $"Your job now is to scan the diff for the persona violations the developer agent " +
           $"should have caught (correctness, security, missing tests, nullable/async/DI issues, " +
           $"secrets in code or logs, unrelated churn). Read the whole diff before deciding.\n\n" +
           $"PR body:\n{context.Body}\n\n" +
           $"Unified diff ({context.ChangedFiles} files, {context.ChangedLines} lines):\n{context.UnifiedDiff}\n\n" +
           $"When done, call submit_review exactly once with your verdict and a markdown summary.";
}
