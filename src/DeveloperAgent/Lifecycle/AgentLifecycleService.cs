using DeveloperAgent.Configuration;
using DeveloperAgent.GitHub;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Lifecycle;

/// <summary>
/// Long-running hosted service that polls for ready GitHub Project items and
/// drives each one through the per-item state machine via <see cref="TaskExecutor"/>.
/// Sequential: one item at a time. Next item is picked on the next timer tick.
/// </summary>
public sealed class AgentLifecycleService(
    ILogger<AgentLifecycleService> logger,
    IOptions<AgentOptions> agentOptions,
    IOptions<GitHubOptions> gitHubOptions,
    IGitHubProjectService github,
    TaskExecutor taskExecutor,
    ITaskStateStore taskStateStore,
    TimeProvider timeProvider) : BackgroundService
{
    private readonly AgentOptions _options = agentOptions.Value;
    private readonly GitHubOptions _gitHubOptions = gitHubOptions.Value;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ── Startup: log configured repo/project and ready-item count ────────
        try
        {
            if (!string.IsNullOrWhiteSpace(_gitHubOptions.Owner) &&
                !string.IsNullOrWhiteSpace(_gitHubOptions.Repository.Name))
            {
                logger.LogInformation(
                    "Repository found: {Owner}/{Repo}",
                    _gitHubOptions.Owner, _gitHubOptions.Repository.Name);

                logger.LogInformation(
                    "Project found: #{Number} \"{Name}\"",
                    _gitHubOptions.Project.Number, _gitHubOptions.Project.Name);

                var readyCount = await github.GetReadyItemCountAsync(stoppingToken);
                logger.LogInformation("Ready items in project: {Count}", readyCount);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GitHubNotConfiguredException ex)
        {
            logger.LogWarning("GitHub not configured — startup status check skipped. {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Startup ready-count check failed (likely misconfigured GitHub credentials). " +
                "Message={Message}", ex.Message);
        }

        // ── Startup: log any items already in-flight (phase-1 skips recovery) ──
        // Tolerate misconfiguration here so dotnet run boots even without real GitHub
        // credentials. The per-tick loop below catches its own exceptions and degrades
        // gracefully (item-by-item failure handling).
        try
        {
            var inFlight = await github.GetInFlightItemsAsync(stoppingToken);
            if (inFlight.Count > 0)
            {
                logger.LogWarning(
                    "Items already in InProgress/InReview at startup; skipping in phase 1 (recovery is phase 2). Count={Count}",
                    inFlight.Count);

                foreach (var item in inFlight)
                {
                    logger.LogWarning(
                        "In-flight item skipped. {ItemId} {IssueNumber} {Title} {State}",
                        item.ProjectItemId, item.ContentNumber, item.Title, item.State);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GitHubNotConfiguredException ex)
        {
            logger.LogWarning("GitHub not configured — poll loop will idle. {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Startup in-flight check failed (likely misconfigured GitHub credentials or unreachable API). " +
                "The poll loop will start anyway; per-tick failures are handled in the loop. Message={Message}",
                ex.Message);
        }

        // ── Outer poll loop ────────────────────────────────────────────────────
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(_options.PollIntervalSeconds),
            timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            GitHub.ProjectItem? item;
            try
            {
                item = await github.TryGetNextReadyItemAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (GitHubNotConfiguredException ex)
            {
                logger.LogDebug("Poll tick skipped — GitHub not configured. {Message}", ex.Message);
                continue;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to poll for ready items this tick (likely transient GitHub error). " +
                    "Skipping tick; will retry on next interval. Message={Message}",
                    ex.Message);
                continue;
            }

            if (item is null)
            {
                logger.LogInformation("DeveloperAgent is waiting for Ready items to work on.");
                continue;
            }

            logger.LogInformation(
                "Ready item found. {ItemId} {IssueNumber} {Title}",
                item.ProjectItemId, item.ContentNumber, item.Title);

            try
            {
                await taskExecutor.RunAsync(item, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Task {ItemId} failed unexpectedly. Message={Message}",
                    item.ProjectItemId, ex.Message);

                try
                {
                    await github.AddItemCommentAsync(
                        item.ContentNodeId,
                        $"Agent crashed: {ex.Message}",
                        stoppingToken);
                }
                catch (Exception commentEx)
                {
                    logger.LogError(commentEx,
                        "Failed to post crash comment for {ItemId}",
                        item.ProjectItemId);
                }

                try
                {
                    await github.MoveItemAsync(
                        item.ProjectItemId,
                        ProjectState.InProgress,
                        ProjectState.Ready,
                        stoppingToken);
                }
                catch (Exception moveEx)
                {
                    logger.LogError(moveEx,
                        "Failed to release {ItemId} back to Ready after crash",
                        item.ProjectItemId);
                }
            }
            finally
            {
                taskStateStore.Clear();
            }
        }
    }
}
