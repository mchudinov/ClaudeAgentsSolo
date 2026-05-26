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
        ReviewPollIntervalSeconds = 10,
        MaxModelTurnsHardCap = 40
    };

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

    private AgentLifecycleService BuildService(
        IGitHubProjectService github,
        IDaprWorkflowClient daprWorkflowClient,
        ITaskStateStore stateStore,
        FakeTimeProvider timeProvider,
        ILogger<AgentLifecycleService>? logger = null,
        GitHubOptions? gitHubOptions = null) =>
        new(logger ?? NullLogger<AgentLifecycleService>.Instance,
            OptionsFactory.Create(Options),
            OptionsFactory.Create(gitHubOptions ?? DefaultGitHubOptions()),
            github,
            daprWorkflowClient,
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
            github,
            daprWorkflowClient,
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
            .Returns(new PullRequestStatus(7, PullRequestReviewState.Pending, false, false, "abc"));

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
    public async Task Recovery_InReview_PR_merged_out_of_band_moves_to_Done()
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
            .Returns(new PullRequestStatus(7, PullRequestReviewState.Approved, true, true, "abc"));

        var service = BuildService(github, daprWorkflowClient, stateStore, timeProvider);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(150);

        await github.Received(1).MoveItemAsync(
            "item-1",
            ProjectState.InReview,
            ProjectState.Done,
            Arg.Any<CancellationToken>());

        // No workflow should be scheduled for already-merged items
        await daprWorkflowClient.DidNotReceive().ScheduleNewWorkflowAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>());

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
    public async Task Workflow_schedule_failure_posts_crash_comment_and_releases_item()
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

        // Comment should contain "Agent crashed"
        await github.Received(1).AddItemCommentAsync(
            readyItem.ContentNodeId,
            Arg.Is<string>(s => s.Contains("Agent crashed")),
            Arg.Any<CancellationToken>());

        // Item should be moved back to Ready
        await github.Received(1).MoveItemAsync(
            readyItem.ProjectItemId,
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
}

file static class OptionsFactory
{
    public static IOptions<T> Create<T>(T value) where T : class =>
        Microsoft.Extensions.Options.Options.Create(value);
}
