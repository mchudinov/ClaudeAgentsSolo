using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReviewerAgent.Configuration;

namespace ReviewerAgent.Lifecycle;

/// <summary>
/// Polls the configured repository's open PRs on an interval and reviews each PR not already
/// reviewed (by the configured bot login) at its current head SHA. Stateless: GitHub is the
/// record of what was reviewed, so a restart re-derives the work from GitHub.
/// </summary>
public sealed class ReviewLifecycleService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ReviewPollingOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReviewLifecycleService> _logger;

    public ReviewLifecycleService(
        IServiceProvider serviceProvider,
        IOptions<ReviewPollingOptions> options,
        TimeProvider timeProvider,
        ILogger<ReviewLifecycleService> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Idempotency keys on (PR, head SHA, ReviewerLogin). A blank login matches no review,
        // so every sweep would re-review (and re-post on) every open PR. Warn loudly rather than
        // silently spamming reviews — the operator must set Reviewer:ReviewerLogin to the bot account.
        if (string.IsNullOrWhiteSpace(_options.ReviewerLogin))
        {
            _logger.LogWarning(
                "Reviewer:ReviewerLogin is not configured. Idempotency is disabled: every poll will " +
                "re-review and re-post on all open PRs. Set it to the GitHub login the token authenticates as.");
        }

        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(_options.PollIntervalSeconds), _timeProvider);

        // Sweep once immediately, then on every tick.
        do
        {
            try
            {
                await ProcessOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Review sweep failed; will retry on the next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Runs one open-PR sweep: review every PR that is due. Public for tests.</summary>
    public async Task ProcessOnceAsync(CancellationToken ct)
    {
        var gitHub = _serviceProvider.GetRequiredService<IGitHubProjectsClient>();
        var reviewer = _serviceProvider.GetRequiredService<IReviewerAgent>();

        var open = await gitHub.ListOpenPullRequestsAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Review sweep: {Count} open PR(s).", open.Count);

        foreach (var pr in open)
        {
            if (await IsDueAsync(gitHub, pr, ct).ConfigureAwait(false))
            {
                _logger.LogInformation("Reviewing PR #{Number} (head {Sha}).", pr.Number, pr.HeadSha);
                try
                {
                    await reviewer.ReviewAsync(pr.Number, ct).ConfigureAwait(false);
                }
                catch (SelfReviewNotAllowedException ex)
                {
                    // GitHub rejected the review because the reviewer authenticates as the PR's own
                    // author — it forbids approving/requesting-changes on your own PR (422). This is a
                    // per-PR misconfiguration, not a transient fault: skip this PR (don't abort the rest
                    // of the sweep) and log loudly with the fix. ex.Message carries GitHub's own 422 text.
                    _logger.LogError(ex,
                        "Cannot review PR #{Number}: GitHub rejected the review — \"{GitHubMessage}\". " +
                        "The ReviewerAgent is authenticating as the PR author (the same GitHub identity as " +
                        "the DeveloperAgent), and GitHub does not allow approving or requesting changes on " +
                        "your own pull request. Give the ReviewerAgent a distinct GitHub identity (a separate " +
                        "bot account or a GitHub App installation token) so its approvals are accepted. " +
                        "Skipping this PR.",
                        pr.Number, ex.Message);
                }
            }
        }
    }

    /// <summary>True when this PR should be reviewed now (not draft-skipped, allowed author, head not yet reviewed).</summary>
    public async Task<bool> IsDueAsync(IGitHubProjectsClient gitHub, OpenPullRequest pr, CancellationToken ct)
    {
        if (_options.SkipDrafts && pr.IsDraft)
            return false;

        if (_options.AuthorAllowList.Count > 0 &&
            !_options.AuthorAllowList.Contains(pr.Author, StringComparer.OrdinalIgnoreCase))
            return false;

        var reviewedShas = await gitHub
            .GetReviewedHeadShasAsync(pr.Number, _options.ReviewerLogin, ct).ConfigureAwait(false);

        return !reviewedShas.Contains(pr.HeadSha, StringComparer.Ordinal);
    }
}
