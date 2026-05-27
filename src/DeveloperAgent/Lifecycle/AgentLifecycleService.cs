using Dapr.Workflow;
using DeveloperAgent.Actors;
using DeveloperAgent.Configuration;
using DeveloperAgent.GitHub;
using DeveloperAgent.Workflow;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Lifecycle;

/// <summary>
/// Long-running hosted service that polls for ready GitHub Project items and
/// schedules a <see cref="DeveloperTaskWorkflow"/> instance per item via
/// <see cref="IDaprWorkflowClient"/>. Workflow instance ID convention:
/// <c>github-project-item-{projectItemId}</c>.
/// Sequential: one item is dispatched per poll tick. Next item is picked on the next tick.
/// </summary>
public sealed class AgentLifecycleService(
    ILogger<AgentLifecycleService> logger,
    IOptions<AgentOptions> agentOptions,
    IOptions<GitHubOptions> gitHubOptions,
    IGitHubProjectService github,
    IDaprWorkflowClient daprWorkflowClient,
    ITaskStateStore taskStateStore,
    TimeProvider timeProvider) : BackgroundService
{
    private readonly AgentOptions _options = agentOptions.Value;
    private readonly GitHubOptions _gitHubOptions = gitHubOptions.Value;

    /// <summary>Workflow instance ID convention used by the lifecycle service.</summary>
    public static string WorkflowInstanceId(string projectItemId) =>
        $"github-project-item-{projectItemId}";

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ── Startup: validate that the configured repo and project exist ─────
        try
        {
            if (!string.IsNullOrWhiteSpace(_gitHubOptions.Owner) &&
                !string.IsNullOrWhiteSpace(_gitHubOptions.Repository.Name))
            {
                var repoExists = await github.RepositoryExistsAsync(stoppingToken);
                if (!repoExists)
                    logger.LogWarning(
                        "Repository {Owner}/{Repo} not found or not accessible on GitHub. " +
                        "Check GitHub.Owner and GitHub.Repository.Name in configuration.",
                        _gitHubOptions.Owner, _gitHubOptions.Repository.Name);

                var projectExists = await github.ProjectExistsAsync(stoppingToken);
                if (!projectExists)
                    logger.LogWarning(
                        "Project #{Number} \"{Name}\" not found or not accessible on GitHub. " +
                        "Check GitHub.Project.Number and GitHub.Project.OwnerType in configuration.",
                        _gitHubOptions.Project.Number, _gitHubOptions.Project.Name);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GitHubNotConfiguredException ex)
        {
            logger.LogWarning("GitHub not configured — config validation skipped. {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Startup config validation failed. Message={Message}", ex.Message);
        }

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

        // ── Startup: recover in-flight items ─────────────────────────────────
        // Enumerate items in InProgress / InReview. For each, check persisted actor state
        // and schedule (or skip) a workflow instance accordingly.
        // Errors per item are logged but do not crash the service — the poll loop starts regardless.
        try
        {
            var inFlight = await github.GetInFlightItemsAsync(stoppingToken);
            foreach (var item in inFlight)
            {
                ProgrammingTaskState? actorState;
                try
                {
                    actorState = await taskStateStore.TryGetPersistedStateAsync(item.ProjectItemId, stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Could not read persisted state for in-flight item {ItemId}; skipping recovery. Message={Message}",
                        item.ProjectItemId, ex.Message);
                    continue;
                }

                if (actorState is null)
                {
                    logger.LogWarning(
                        "In-flight item has no persisted actor state; skipping. {ItemId} {IssueNumber} {Title} {State}",
                        item.ProjectItemId, item.ContentNumber, item.Title, item.State);
                    continue;
                }

                if (item.State == ProjectState.InReview &&
                    actorState.PullRequestNumber is int prNumber)
                {
                    PullRequestStatus status;
                    try
                    {
                        status = await github.GetPullRequestStatusAsync(prNumber, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex,
                            "Could not read PR status for in-flight item {ItemId} PR#{PrNumber}; skipping recovery. Message={Message}",
                            item.ProjectItemId, prNumber, ex.Message);
                        continue;
                    }

                    if (status.Merged)
                    {
                        // PR merged out-of-band before restart. Schedule a workflow with the
                        // RecoveryAlreadyMerged fast-path: DoneActivity will perform the
                        // InReview → Done transition. State ownership lives in the workflow.
                        logger.LogInformation(
                            "PR merged out-of-band before restart; dispatching recovery workflow. {ItemId} PR#{PrNumber}",
                            item.ProjectItemId, prNumber);

                        await ScheduleWorkflowAsync(
                            item,
                            stoppingToken,
                            recoveryAlreadyMerged: true,
                            recoveryPrNumber: prNumber,
                            recoveryBranchName: actorState.BranchName ?? string.Empty);
                        continue;
                    }

                    // InReview with open PR — reschedule workflow to resume from where it left off.
                    logger.LogInformation(
                        "Rescheduling workflow for in-flight InReview item. {ItemId} PR#{PrNumber}",
                        item.ProjectItemId, prNumber);

                    await ScheduleWorkflowAsync(item, stoppingToken);
                    continue;
                }

                // InProgress (with or without PR) or InReview without a known PR number → re-schedule from scratch.
                logger.LogInformation(
                    "Rescheduling workflow for in-flight item after restart. {ItemId} Phase={Phase} PR={PR}",
                    item.ProjectItemId, actorState.Phase, actorState.PullRequestNumber);

                await ScheduleWorkflowAsync(item, stoppingToken);
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
                await ScheduleWorkflowAsync(item, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Step-15: lifecycle is a thin dispatcher. State transitions belong to the
                // workflow (DoneActivity owns InProgress → Ready on failure; the workflow
                // never started here so the item is still in Ready — no rollback needed).
                logger.LogError(ex,
                    "Failed to schedule workflow for {ItemId}. Item stays in Ready; will retry on next tick. Message={Message}",
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
            }
        }
    }

    private async Task ScheduleWorkflowAsync(
        GitHub.ProjectItem item,
        CancellationToken ct,
        bool recoveryAlreadyMerged = false,
        int? recoveryPrNumber = null,
        string recoveryBranchName = "")
    {
        var instanceId = WorkflowInstanceId(item.ProjectItemId);
        var input = new TaskInput(
            ProjectItemId: item.ProjectItemId,
            ContentNodeId: item.ContentNodeId,
            ContentNumber: item.ContentNumber,
            Title: item.Title,
            BodyMarkdown: item.BodyMarkdown)
        {
            MaxRetryAttempts = _options.MaxRetryAttempts,
            FirstRetryIntervalSeconds = _options.FirstRetryIntervalSeconds,
            RecoveryAlreadyMerged = recoveryAlreadyMerged,
            RecoveryPullRequestNumber = recoveryPrNumber,
            RecoveryBranchName = recoveryBranchName
        };

        await daprWorkflowClient.ScheduleNewWorkflowAsync(
            name: nameof(DeveloperTaskWorkflow),
            instanceId: instanceId,
            input: input);

        logger.LogInformation(
            "Workflow scheduled. instanceId={InstanceId} item={ItemId} recoveryAlreadyMerged={Recovery}",
            instanceId, item.ProjectItemId, recoveryAlreadyMerged);
    }
}
