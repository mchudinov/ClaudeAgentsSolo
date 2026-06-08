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
                await reviewer.ReviewAsync(pr.Number, ct).ConfigureAwait(false);
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
