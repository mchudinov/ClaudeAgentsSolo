using Agent.Workflow;
using Dapr.Workflow;
using DeveloperAgent.Actors;
using DeveloperAgent.Configuration;
using DeveloperAgent.GitHub;
using DeveloperAgent.Lifecycle;
using DeveloperAgent.Workflow;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DeveloperAgent.Tests.Lifecycle;

/// <summary>
/// Unit tests for <see cref="AgentLifecycleService"/>.
/// The service now dispatches <see cref="DeveloperTaskWorkflow"/> instances via
/// <see cref="IDaprWorkflowClient"/> rather than calling <see cref="ITaskExecutor"/> directly.
/// Tests verify that the correct workflow schedule calls are made in each scenario.
/// </summary>
public sealed class AgentLifecycleServiceTests
{
    private static readonly AgentOptions Options = new()
    {
        PollIntervalSeconds = 10,
        ReviewPollIntervalSeconds = 10
    };

    private static readonly ScopeLimitOptions ScopeLimits = new();

    private static ProjectItem MakeItem(string id = "item-1", ProjectState state = ProjectState.InProgress) =>
        new(
            ProjectItemId: id,
            ContentNodeId: $"content-{id}",
            ContentNumber: 42,
            Title: "Add feature X",
            BodyMarkdown: "Implement feature X",
            State: state);

    private static GitHubOptions DefaultGitHubOptions() => new()
    {
        Owner = "test-org",
        Repository = new RepositoryOptions { Name = "test-repo", DefaultBranch = "main" },
        Project = new ProjectOptions { Number = 1, Name = "TestProject", OwnerType = "Organization" }
    };

    /// <summary>
    /// An inspector that reports every instance id as <see cref="WorkflowInstanceDisposition.NotFound"/>
    /// (the default for a fresh substitute too) — i.e. "no conflicting instance, schedule freely".
    /// Tests exercising the skip/purge branches pass an explicitly-configured inspector instead.
    /// </summary>
    private static IWorkflowInstanceInspector NotFoundInspector()
    {
        var inspector = Substitute.For<IWorkflowInstanceInspector>();
        inspector.GetDispositionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WorkflowInstanceDisposition.NotFound);
        return inspector;
    }

    private AgentLifecycleService BuildService(
        IGitHubProjectService github,
        IDaprWorkflowClient daprWorkflowClient,
        ITaskStateStore stateStore,
        FakeTimeProvider timeProvider,
        ILogger<AgentLifecycleService>? logger = null,
        GitHubOptions? gitHubOptions = null,
        IWorkflowInstanceInspector? workflowInspector = null) =>
        new(logger ?? NullLogger<AgentLifecycleService>.Instance,
            OptionsFactory.Create(Options),
            OptionsFactory.Create(gitHubOptions ?? DefaultGitHubOptions()),
            OptionsFactory.Create(ScopeLimits),
            github,
            daprWorkflowClient,
            workflowInspector ?? NotFoundInspector(),
            stateStore,
            timeProvider);

    // ── Startup: in-flight items with no persisted state ──────────────────────

    [Fact]
    public async Task Startup_logs_in_flight_items_and_skips_when_no_actor_state()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = Substitute.For<ITaskStateStore>();
        var daprWorkflowClient = Substitute.For<IDaprWorkflowClient>();
        var timeProvider = new FakeTimeProvider();
        var logger = Substitute.For<ILogger<AgentLifecycleService>>();

        var inFlightItems = new[] { MakeItem("item-1"), MakeItem("item-2") };
        github.GetInFlightItemsAsync(Arg.Any<CancellationToken>())
            .Returns(inFlightItems);
        github.TryGetNextReadyItemAsync(Arg.Any<CancellationToken>())
            .Returns((ProjectItem?)null);

        // Both items have no persisted actor state — recovery skips them
        stateStore.TryGetPersistedStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ProgrammingTaskState?)null);

        var service = new AgentLifecycleService(
            logger,
            OptionsFactory.Create(Options),
            OptionsFactory.Create(DefaultGitHubOptions()),
            OptionsFactory.Create(ScopeLimits),
            github,
            daprWorkflowClient,
            NotFoundInspector(),
            stateStore,
            timeProvider);

        using var cts = new CancellationTokenSource();
        var executeTask = service.StartAsync(cts.Token);
        await executeTask;

        await Task.Delay(50);

        // Logger should have been called with LogWarning for the in-flight items with no state
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        // No workflow should be scheduled for items with no persisted state
        await daprWorkflowClient.DidNotReceive().ScheduleNewWorkflowAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>());

        cts.Cancel();
    }

    // ── Startup: recovery — InProgress ────────────────────────────────────────

    [Fact]
    public async Task Recovery_InProgress_schedules_workflow()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = Substitute.For<ITaskStateStore>();
        var daprWorkflowClient = Substitute.For<IDaprWorkflowClient>();
        var timeProvider = new FakeTimeProvider();

        var item = MakeItem("item-1", ProjectState.InProgress);
        github.GetInFlightItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { item });
        github.TryGetNextReadyItemAsync(Arg.Any<CancellationToken>())
            .Returns((ProjectItem?)null);

        var actorState = new ProgrammingTaskState(
            ProjectItemId: "item-1",
            AgentId: "agent-1",
            Phase: TaskPhase.AgentRunning,
            BranchName: "agent/add-feature-x",
            PullRequestNumber: null,
            RetryCount: 0,
            ApprovalStatus: ApprovalStatus.None);

        stateStore.TryGetPersistedStateAsync("item-1", Arg.Any<CancellationToken>())
            .Returns(actorState);

        var service = BuildService(github, daprWorkflowClient, stateStore, timeProvider);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(150);

        await daprWorkflowClient.Received(1).ScheduleNewWorkflowAsync(
            nameof(DeveloperTaskWorkflow),
            AgentLifecycleService.WorkflowInstanceId("item-1"),
            Arg.Any<object>());

        cts.Cancel();
    }

    [Fact]
    public async Task Recovery_InProgress_with_PR_schedules_workflow()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = Substitute.For<ITaskStateStore>();
        var daprWorkflowClient = Substitute.For<IDaprWorkflowClient>();
        var timeProvider = new FakeTimeProvider();

        var item = MakeItem("item-1", ProjectState.InProgress);
        github.GetInFlightItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { item });
        github.TryGetNextReadyItemAsync(Arg.Any<CancellationToken>())
            .Returns((ProjectItem?)null);

        var actorState = new ProgrammingTaskState(
            ProjectItemId: "item-1",
            AgentId: "agent-1",
            Phase: TaskPhase.AgentRunning,
            BranchName: "agent/add-feature-x",
            PullRequestNumber: 7,
            RetryCount: 0,
            ApprovalStatus: ApprovalStatus.ChangesRequested);

        stateStore.TryGetPersistedStateAsync("item-1", Arg.Any<CancellationToken>())
            .Returns(actorState);

        var service = BuildService(github, daprWorkflowClient, stateStore, timeProvider);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(150);

        await daprWorkflowClient.Received(1).ScheduleNewWorkflowAsync(
            nameof(DeveloperTaskWorkflow),
            AgentLifecycleService.WorkflowInstanceId("item-1"),
            Arg.Any<object>());

        cts.Cancel();
    }

    // ── Startup: recovery — InReview ──────────────────────────────────────────

    [Fact]
    public async Task Recovery_InReview_PR_open_schedules_workflow()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = Substitute.For<ITaskStateStore>();
        var daprWorkflowClient = Substitute.For<IDaprWorkflowClient>();
        var timeProvider = new FakeTimeProvider();

        var item = MakeItem("item-1", ProjectState.InReview);
        github.GetInFlightItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { item });
        github.TryGetNextReadyItemAsync(Arg.Any<CancellationToken>())
            .Returns((ProjectItem?)null);

        var actorState = new ProgrammingTaskState(
            ProjectItemId: "item-1",
            AgentId: "agent-1",
            Phase: TaskPhase.AwaitingReview,
            BranchName: "agent/add-feature-x",
            PullRequestNumber: 7,
            RetryCount: 0,
            ApprovalStatus: ApprovalStatus.WaitingForReview);

        stateStore.TryGetPersistedStateAsync("item-1", Arg.Any<CancellationToken>())
            .Returns(actorState);

        github.GetPullRequestStatusAsync(7, Arg.Any<CancellationToken>())
            .Returns(new PullRequestStatus(7, PullRequestReviewState.Pending, false, false, "abc", null));

        var service = BuildService(github, daprWorkflowClient, stateStore, timeProvider);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(150);

        await daprWorkflowClient.Received(1).ScheduleNewWorkflowAsync(
            nameof(DeveloperTaskWorkflow),
            AgentLifecycleService.WorkflowInstanceId("item-1"),
            Arg.Any<object>());

        cts.Cancel();
    }

    [Fact]
    public async Task Recovery_InReview_PR_merged_out_of_band_schedules_recovery_workflow()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = Substitute.For<ITaskStateStore>();
        var daprWorkflowClient = Substitute.For<IDaprWorkflowClient>();
        var timeProvider = new FakeTimeProvider();

        var item = MakeItem("item-1", ProjectState.InReview);
        github.GetInFlightItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { item });
        github.TryGetNextReadyItemAsync(Arg.Any<CancellationToken>())
            .Returns((ProjectItem?)null);

        var actorState = new ProgrammingTaskState(
            ProjectItemId: "item-1",
            AgentId: "agent-1",
            Phase: TaskPhase.AwaitingReview,
            BranchName: "agent/add-feature-x",
            PullRequestNumber: 7,
            RetryCount: 0,
            ApprovalStatus: ApprovalStatus.WaitingForReview);

        stateStore.TryGetPersistedStateAsync("item-1", Arg.Any<CancellationToken>())
            .Returns(actorState);

        github.GetPullRequestStatusAsync(7, Arg.Any<CancellationToken>())
            .Returns(new PullRequestStatus(7, PullRequestReviewState.Approved, true, true, "abc", null));

        var service = BuildService(github, daprWorkflowClient, stateStore, timeProvider);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(150);

        // Lifecycle no longer calls MoveItemAsync directly — the workflow's DoneActivity owns
        // the state transition. Instead, a workflow instance is scheduled with the recovery flag.
        await github.DidNotReceive().MoveItemAsync(
            Arg.Any<string>(),
            ProjectState.InReview,
            ProjectState.Done,
            Arg.Any<CancellationToken>());

        await daprWorkflowClient.Received(1).ScheduleNewWorkflowAsync(
            nameof(DeveloperTaskWorkflow),
            AgentLifecycleService.WorkflowInstanceId("item-1"),
            Arg.Is<object>(o => CheckRecoveryInput(o, expectedPrNumber: 7)));

        cts.Cancel();
    }

    // ── Idempotent scheduling (Step-34): existing instance under the same id ──

    [Fact]
    public async Task Schedule_skips_when_workflow_instance_already_active()
    {
        // Reproduces the reported crash: on restart, recovery reschedules an InReview item whose
        // workflow instance still exists and is non-terminal. Dapr rejects a second schedule under
        // the same deterministic id ("an active workflow with ID ... already exists"). The runtime
        // resumes active instances on its own, so the service must skip — not reschedule, not purge.
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = Substitute.For<ITaskStateStore>();
        var daprWorkflowClient = Substitute.For<IDaprWorkflowClient>();
        var timeProvider = new FakeTimeProvider();

        var item = MakeItem("item-1", ProjectState.InReview);
        github.GetInFlightItemsAsync(Arg.Any<CancellationToken>()).Returns(new[] { item });
        github.TryGetNextReadyItemAsync(Arg.Any<CancellationToken>()).Returns((ProjectItem?)null);

        var actorState = new ProgrammingTaskState(
            ProjectItemId: "item-1",
            AgentId: "agent-1",
            Phase: TaskPhase.AwaitingReview,
            BranchName: "agent/add-feature-x",
            PullRequestNumber: 7,
            RetryCount: 0,
            ApprovalStatus: ApprovalStatus.WaitingForReview);
        stateStore.TryGetPersistedStateAsync("item-1", Arg.Any<CancellationToken>()).Returns(actorState);
        github.GetPullRequestStatusAsync(7, Arg.Any<CancellationToken>())
            .Returns(new PullRequestStatus(7, PullRequestReviewState.Pending, false, false, "abc", null));

        var inspector = Substitute.For<IWorkflowInstanceInspector>();
        inspector.GetDispositionAsync(
                AgentLifecycleService.WorkflowInstanceId("item-1"), Arg.Any<CancellationToken>())
            .Returns(WorkflowInstanceDisposition.Active);

        var service = BuildService(github, daprWorkflowClient, stateStore, timeProvider, workflowInspector: inspector);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(150);

        await daprWorkflowClient.DidNotReceive().ScheduleNewWorkflowAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>());
        await daprWorkflowClient.DidNotReceive().PurgeInstanceAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());

        cts.Cancel();
    }

    [Fact]
    public async Task Schedule_purges_terminal_instance_then_reschedules()
    {
        // A prior run left a terminal (Completed/Failed/…) instance record occupying the
        // deterministic id. The service must purge it, then schedule a fresh instance.
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = Substitute.For<ITaskStateStore>();
        var daprWorkflowClient = Substitute.For<IDaprWorkflowClient>();
        var timeProvider = new FakeTimeProvider();

        github.GetInFlightItemsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<ProjectItem>());

        var readyItem = MakeItem("item-stale", ProjectState.Ready);
        var callCount = 0;
        github.TryGetNextReadyItemAsync(Arg.Any<CancellationToken>())
            .Returns(_ => { callCount++; return callCount == 1 ? readyItem : (ProjectItem?)null; });

        var instanceId = AgentLifecycleService.WorkflowInstanceId("item-stale");
        var inspector = Substitute.For<IWorkflowInstanceInspector>();
        inspector.GetDispositionAsync(instanceId, Arg.Any<CancellationToken>())
            .Returns(WorkflowInstanceDisposition.Terminal);

        var service = BuildService(github, daprWorkflowClient, stateStore, timeProvider, workflowInspector: inspector);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(50);
        timeProvider.Advance(TimeSpan.FromSeconds(11));
        await Task.Delay(100);

        await daprWorkflowClient.Received(1).PurgeInstanceAsync(instanceId, Arg.Any<CancellationToken>());
        await daprWorkflowClient.Received(1).ScheduleNewWorkflowAsync(
            nameof(DeveloperTaskWorkflow), instanceId, Arg.Any<object>());

        cts.Cancel();
    }

    [Fact]
    public async Task Schedule_does_not_purge_when_no_existing_instance()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = Substitute.For<ITaskStateStore>();
        var daprWorkflowClient = Substitute.For<IDaprWorkflowClient>();
        var timeProvider = new FakeTimeProvider();

        github.GetInFlightItemsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<ProjectItem>());

        var readyItem = MakeItem("item-fresh", ProjectState.Ready);
        var callCount = 0;
        github.TryGetNextReadyItemAsync(Arg.Any<CancellationToken>())
            .Returns(_ => { callCount++; return callCount == 1 ? readyItem : (ProjectItem?)null; });

        // Default NotFoundInspector → no conflicting instance.
        var service = BuildService(github, daprWorkflowClient, stateStore, timeProvider);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(50);
        timeProvider.Advance(TimeSpan.FromSeconds(11));
        await Task.Delay(100);

        await daprWorkflowClient.DidNotReceive().PurgeInstanceAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await daprWorkflowClient.Received(1).ScheduleNewWorkflowAsync(
            nameof(DeveloperTaskWorkflow),
            AgentLifecycleService.WorkflowInstanceId("item-fresh"),
            Arg.Any<object>());

        cts.Cancel();
    }

    // ── Poll loop ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task No_Ready_item_on_tick_continues_loop()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = Substitute.For<ITaskStateStore>();
        var daprWorkflowClient = Substitute.For<IDaprWorkflowClient>();
        var timeProvider = new FakeTimeProvider();
        var logger = Substitute.For<ILogger<AgentLifecycleService>>();

        github.GetInFlightItemsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ProjectItem>());
        github.TryGetNextReadyItemAsync(Arg.Any<CancellationToken>())
            .Returns((ProjectItem?)null);

        var service = BuildService(github, daprWorkflowClient, stateStore, timeProvider, logger);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(50);

        timeProvider.Advance(TimeSpan.FromSeconds(11));
        await Task.Delay(100);

        logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("waiting")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        await daprWorkflowClient.DidNotReceive().ScheduleNewWorkflowAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>());

        cts.Cancel();
    }

    [Fact]
    public async Task Ready_item_found_schedules_workflow_with_correct_instance_id()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = Substitute.For<ITaskStateStore>();
        var daprWorkflowClient = Substitute.For<IDaprWorkflowClient>();
        var timeProvider = new FakeTimeProvider();

        github.GetInFlightItemsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ProjectItem>());

        var readyItem = MakeItem("item-ready", ProjectState.Ready);
        var callCount = 0;
        github.TryGetNextReadyItemAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1 ? readyItem : (ProjectItem?)null;
            });

        var service = BuildService(github, daprWorkflowClient, stateStore, timeProvider);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(50);

        timeProvider.Advance(TimeSpan.FromSeconds(11));
        await Task.Delay(100);

        await daprWorkflowClient.Received(1).ScheduleNewWorkflowAsync(
            nameof(DeveloperTaskWorkflow),
            "github-project-item-item-ready",
            Arg.Any<object>());

        cts.Cancel();
    }

    [Fact]
    public async Task Workflow_schedule_failure_logs_and_skips_without_moving_item()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = Substitute.For<ITaskStateStore>();
        var daprWorkflowClient = Substitute.For<IDaprWorkflowClient>();
        var timeProvider = new FakeTimeProvider();

        github.GetInFlightItemsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ProjectItem>());

        var readyItem = MakeItem("item-crash", ProjectState.Ready);
        github.TryGetNextReadyItemAsync(Arg.Any<CancellationToken>())
            .Returns(readyItem);

        // Scheduling the workflow throws
        daprWorkflowClient
            .ScheduleNewWorkflowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>())
            .Returns<string>(_ => throw new InvalidOperationException("dapr not available"));

        using var cts = new CancellationTokenSource();
        var service = BuildService(github, daprWorkflowClient, stateStore, timeProvider);
        await service.StartAsync(cts.Token);
        await Task.Delay(50);

        timeProvider.Advance(TimeSpan.FromSeconds(11));
        await Task.Delay(200);

        // Step-15: lifecycle no longer owns state transitions. On dispatch failure it
        // logs and continues. The item stays in Ready (no Acquire ran) so any rollback
        // would be a no-op. State ownership lives in DoneActivity.
        await github.DidNotReceive().MoveItemAsync(
            Arg.Any<string>(),
            ProjectState.InProgress,
            ProjectState.Ready,
            Arg.Any<CancellationToken>());

        cts.Cancel();
    }

    // ── Startup validation ────────────────────────────────────────────────────

    [Fact]
    public async Task Startup_logs_warning_when_repository_does_not_exist()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = Substitute.For<ITaskStateStore>();
        var daprWorkflowClient = Substitute.For<IDaprWorkflowClient>();
        var timeProvider = new FakeTimeProvider();
        var logger = Substitute.For<ILogger<AgentLifecycleService>>();

        github.GetInFlightItemsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<ProjectItem>());
        github.GetReadyItemCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        github.TryGetNextReadyItemAsync(Arg.Any<CancellationToken>()).Returns((ProjectItem?)null);
        github.RepositoryExistsAsync(Arg.Any<CancellationToken>()).Returns(false);
        github.ProjectExistsAsync(Arg.Any<CancellationToken>()).Returns(true);

        var service = BuildService(github, daprWorkflowClient, stateStore, timeProvider, logger);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(100);

        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("not found") || o.ToString()!.Contains("Repository")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        cts.Cancel();
    }

    [Fact]
    public async Task Startup_logs_warning_when_project_does_not_exist()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = Substitute.For<ITaskStateStore>();
        var daprWorkflowClient = Substitute.For<IDaprWorkflowClient>();
        var timeProvider = new FakeTimeProvider();
        var logger = Substitute.For<ILogger<AgentLifecycleService>>();

        github.GetInFlightItemsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<ProjectItem>());
        github.GetReadyItemCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        github.TryGetNextReadyItemAsync(Arg.Any<CancellationToken>()).Returns((ProjectItem?)null);
        github.RepositoryExistsAsync(Arg.Any<CancellationToken>()).Returns(true);
        github.ProjectExistsAsync(Arg.Any<CancellationToken>()).Returns(false);

        var service = BuildService(github, daprWorkflowClient, stateStore, timeProvider, logger);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(100);

        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("not found") || o.ToString()!.Contains("Project")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        cts.Cancel();
    }

    [Fact]
    public async Task Startup_logs_configured_repo_and_project_at_Information()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = Substitute.For<ITaskStateStore>();
        var daprWorkflowClient = Substitute.For<IDaprWorkflowClient>();
        var timeProvider = new FakeTimeProvider();
        var logger = Substitute.For<ILogger<AgentLifecycleService>>();

        github.GetInFlightItemsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<ProjectItem>());
        github.GetReadyItemCountAsync(Arg.Any<CancellationToken>()).Returns(3);
        github.TryGetNextReadyItemAsync(Arg.Any<CancellationToken>()).Returns((ProjectItem?)null);

        var gitHubOptions = new GitHubOptions
        {
            Owner = "acme-org",
            Repository = new RepositoryOptions { Name = "my-repo" },
            Project = new ProjectOptions { Number = 7, Name = "Sprint Board" }
        };

        var service = BuildService(github, daprWorkflowClient, stateStore, timeProvider, logger, gitHubOptions);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(100);

        logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("acme-org") && o.ToString()!.Contains("my-repo")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Sprint Board")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        cts.Cancel();
    }

    [Fact]
    public async Task Startup_logs_Ready_item_count()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = Substitute.For<ITaskStateStore>();
        var daprWorkflowClient = Substitute.For<IDaprWorkflowClient>();
        var timeProvider = new FakeTimeProvider();
        var logger = Substitute.For<ILogger<AgentLifecycleService>>();

        github.GetInFlightItemsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<ProjectItem>());
        github.GetReadyItemCountAsync(Arg.Any<CancellationToken>()).Returns(5);
        github.TryGetNextReadyItemAsync(Arg.Any<CancellationToken>()).Returns((ProjectItem?)null);

        var service = BuildService(github, daprWorkflowClient, stateStore, timeProvider, logger);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(100);

        logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Ready") && o.ToString()!.Contains("5")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        cts.Cancel();
    }

    [Fact]
    public async Task WorkflowInstanceId_returns_correct_format()
    {
        AgentLifecycleService.WorkflowInstanceId("PVTI_abc123")
            .Should().Be("github-project-item-PVTI_abc123");
    }

    // ── Retry-policy binding (Step-15 P2-D part 3/3) ──────────────────────────

    [Fact]
    public async Task Dispatched_TaskInput_carries_AgentOptions_retry_settings()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = Substitute.For<ITaskStateStore>();
        var daprWorkflowClient = Substitute.For<IDaprWorkflowClient>();
        var timeProvider = new FakeTimeProvider();

        github.GetInFlightItemsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ProjectItem>());

        var readyItem = MakeItem("item-binding", ProjectState.Ready);
        var callCount = 0;
        github.TryGetNextReadyItemAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1 ? readyItem : (ProjectItem?)null;
            });

        // Use non-default values so this test can't pass by accident on defaults.
        // Step-21 (P2-H): TaskInput.MaxRetryAttempts (the workflow activity-retry cap) is
        // sourced from the scope-limit policy's MaxRetryCount. FirstRetryIntervalSeconds
        // (the back-off cadence) still comes from AgentOptions.
        var customOptions = new AgentOptions
        {
            PollIntervalSeconds = 10,
            ReviewPollIntervalSeconds = 10,
            FirstRetryIntervalSeconds = 5
        };
        var customScopeLimits = new ScopeLimitOptions { MaxRetryCount = 7 };

        var service = new AgentLifecycleService(
            NullLogger<AgentLifecycleService>.Instance,
            OptionsFactory.Create(customOptions),
            OptionsFactory.Create(DefaultGitHubOptions()),
            OptionsFactory.Create(customScopeLimits),
            github,
            daprWorkflowClient,
            NotFoundInspector(),
            stateStore,
            timeProvider);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(50);

        timeProvider.Advance(TimeSpan.FromSeconds(11));
        await Task.Delay(100);

        var schedules = daprWorkflowClient.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IDaprWorkflowClient.ScheduleNewWorkflowAsync))
            .ToList();
        schedules.Should().NotBeEmpty(because: "the ready item should have been dispatched");

        var capturedInput = schedules.Last().GetArguments()[2];
        capturedInput.Should().BeOfType<TaskInput>();
        var ti = (TaskInput)capturedInput!;
        ti.MaxRetryAttempts.Should().Be(7);
        ti.FirstRetryIntervalSeconds.Should().Be(5);

        cts.Cancel();
    }

    /// <summary>
    /// Static helper used inside NSubstitute <c>Arg.Is</c> predicates — expression trees
    /// can't contain pattern-matching, so the check is delegated here.
    /// </summary>
    private static bool CheckRecoveryInput(object o, int expectedPrNumber)
    {
        if (o is not TaskInput ti) return false;
        return ti.RecoveryAlreadyMerged && ti.RecoveryPullRequestNumber == expectedPrNumber;
    }
}

file static class OptionsFactory
{
    public static IOptions<T> Create<T>(T value) where T : class =>
        Microsoft.Extensions.Options.Options.Create(value);
}
