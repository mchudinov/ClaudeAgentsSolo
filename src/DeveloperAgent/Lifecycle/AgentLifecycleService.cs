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
    IGitHubProjectService github,
    TaskExecutor taskExecutor,
    ITaskStateStore taskStateStore,
    TimeProvider timeProvider) : BackgroundService
{
    private readonly AgentOptions _options = agentOptions.Value;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ── Startup: log any items already in-flight (phase-1 skips recovery) ──
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

        // ── Outer poll loop ────────────────────────────────────────────────────
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(_options.PollIntervalSeconds),
            timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var item = await github.TryGetNextReadyItemAsync(stoppingToken);
            if (item is null)
            {
                logger.LogDebug("No ready item on this tick.");
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
