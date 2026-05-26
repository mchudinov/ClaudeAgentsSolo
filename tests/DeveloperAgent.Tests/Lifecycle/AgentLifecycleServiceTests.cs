using DeveloperAgent.Actors;
using DeveloperAgent.Agent;
using DeveloperAgent.Configuration;
using DeveloperAgent.GitHub;
using DeveloperAgent.Lifecycle;
using DeveloperAgent.Observability;
using DeveloperAgent.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute.ExceptionExtensions;
using NSubstitute.ReceivedExtensions;

namespace DeveloperAgent.Tests.Lifecycle;

/// <summary>
/// Unit tests for the outer poll loop in <see cref="AgentLifecycleService"/>.
/// Tests drive <see cref="FakeTimeProvider"/> to advance the <see cref="PeriodicTimer"/>
/// deterministically without real delays.
/// </summary>
public sealed class AgentLifecycleServiceTests
{
    private static readonly AgentOptions Options = new()
    {
        PollIntervalSeconds = 10,
        ReviewPollIntervalSeconds = 10,
        MaxModelTurnsHardCap = 40
    };

    private static ProjectItem MakeItem(string id = "item-1") =>
        new(
            ProjectItemId: id,
            ContentNodeId: $"content-{id}",
            ContentNumber: 42,
            Title: "Add feature X",
            BodyMarkdown: "Implement feature X",
            State: ProjectState.InProgress);

    private static TaskWorkspace MakeWorkspace(string itemId = "item-1") =>
        new(ProjectItemId: itemId, BranchName: "agent/add-feature-x-abcd1234",
            RepoRoot: "/workspace/item-1/repo", DefaultBranch: "main");

    private static PullRequest MakePr(int number = 99) =>
        new(Number: number, HeadSha: "abc123", HtmlUrl: $"https://github.com/org/repo/pull/{number}");

    private static GitHubOptions DefaultGitHubOptions() => new()
    {
        Owner = "test-org",
        Repository = new RepositoryOptions { Name = "test-repo", DefaultBranch = "main" },
        Project = new ProjectOptions { Number = 1, Name = "TestProject", OwnerType = "Organization" }
    };

    private AgentLifecycleService BuildService(
        IGitHubProjectService github,
        ITaskExecutor taskExecutor,
        ITaskStateStore stateStore,
        FakeTimeProvider timeProvider,
        ILogger<AgentLifecycleService>? logger = null,
        GitHubOptions? gitHubOptions = null) =>
        new(logger ?? NullLogger<AgentLifecycleService>.Instance,
            OptionsFactory.Create(Options),
            OptionsFactory.Create(gitHubOptions ?? DefaultGitHubOptions()),
            github,
            taskExecutor,
            stateStore,
            timeProvider);

    private static ITaskExecutor BuildTaskExecutor(
        IGitHubProjectService github,
        IWorkspaceManager workspaceMgr,
        IGitClient gitClient,
        IAgentRunner agentRunner,
        ITaskStateStore stateStore,
        FakeTimeProvider timeProvider) =>
        new TaskExecutor(NullLogger<TaskExecutor>.Instance,
            OptionsFactory.Create(Options),
            github,
            workspaceMgr,
            gitClient,
            agentRunner,
            stateStore,
            timeProvider,
            new AgentMetrics());

    [Fact]
    public async Task Startup_logs_in_flight_items_and_skips()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = Substitute.For<ITaskStateStore>();
        var timeProvider = new FakeTimeProvider();
        var logger = Substitute.For<ILogger<AgentLifecycleService>>();

        var inFlightItems = new[] { MakeItem("item-1"), MakeItem("item-2") };
        github.GetInFlightItemsAsync(Arg.Any<CancellationToken>())
            .Returns(inFlightItems);
        github.TryGetNextReadyItemAsync(Arg.Any<CancellationToken>())
            .Returns((ProjectItem?)null);

        // Both items have no persisted actor state — recovery skips them (InMemory returns null)
        stateStore.TryGetPersistedStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ProgrammingTaskState?)null);

        var taskExecutor = Substitute.For<ITaskExecutor>();

        var service = new AgentLifecycleService(
            logger,
            OptionsFactory.Create(Options),
            OptionsFactory.Create(DefaultGitHubOptions()),
            github,
            taskExecutor,
            stateStore,
            timeProvider);

        using var cts = new CancellationTokenSource();
        var executeTask = service.StartAsync(cts.Token);
        await executeTask;

        // Let the service start (GetInFlightItemsAsync is called before the timer loop)
        await Task.Delay(50);

        // Logger should have been called with LogWarning for the in-flight items that have no state
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        // TaskExecutor.RunAsync should NOT be called for in-flight items with no persisted state
        await taskExecutor.DidNotReceive().RunAsync(Arg.Any<ProjectItem>(), Arg.Any<CancellationToken>());

        cts.Cancel();
    }

    [Fact]
    public async Task Recovery_InProgress_no_PR_resumes_agent_run()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = Substitute.For<ITaskStateStore>();
        var timeProvider = new FakeTimeProvider();
        var taskExecutor = Substitute.For<ITaskExecutor>();

        var item = MakeItem("item-1") with { State = ProjectState.InProgress };
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

        taskExecutor.RunAsync(Arg.Any<ProjectItem>(), Arg.Any<CancellationToken>())
            .Returns(new TaskExecutionResult(TaskOutcome.Done));

        var service = BuildService(github, taskExecutor, stateStore, timeProvider);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(150);

        await taskExecutor.Received(1).RunAsync(
            Arg.Is<ProjectItem>(p => p.ProjectItemId == "item-1"),
            Arg.Any<CancellationToken>());

        cts.Cancel();
    }

    [Fact]
    public async Task Recovery_InProgress_with_PR_resumes_agent_run()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = Substitute.For<ITaskStateStore>();
        var timeProvider = new FakeTimeProvider();
        var taskExecutor = Substitute.For<ITaskExecutor>();

        var item = MakeItem("item-1") with { State = ProjectState.InProgress };
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

        taskExecutor.RunAsync(Arg.Any<ProjectItem>(), Arg.Any<CancellationToken>())
            .Returns(new TaskExecutionResult(TaskOutcome.Done));

        var service = BuildService(github, taskExecutor, stateStore, timeProvider);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(150);

        await taskExecutor.Received(1).RunAsync(
            Arg.Is<ProjectItem>(p => p.ProjectItemId == "item-1"),
            Arg.Any<CancellationToken>());

        cts.Cancel();
    }

    [Fact]
    public async Task Recovery_InReview_PR_open_enters_review_wait()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = Substitute.For<ITaskStateStore>();
        var timeProvider = new FakeTimeProvider();
        var taskExecutor = Substitute.For<ITaskExecutor>();

        var item = MakeItem("item-1") with { State = ProjectState.InReview };
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

        taskExecutor.ResumeReviewAsync(Arg.Any<ProjectItem>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new TaskExecutionResult(TaskOutcome.Done));

        var service = BuildService(github, taskExecutor, stateStore, timeProvider);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(150);

        await taskExecutor.Received(1).ResumeReviewAsync(
            Arg.Is<ProjectItem>(p => p.ProjectItemId == "item-1"),
            7,
            Arg.Any<CancellationToken>());

        cts.Cancel();
    }

    [Fact]
    public async Task Recovery_InReview_PR_merged_out_of_band_moves_to_Done()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = Substitute.For<ITaskStateStore>();
        var timeProvider = new FakeTimeProvider();
        var taskExecutor = Substitute.For<ITaskExecutor>();

        var item = MakeItem("item-1") with { State = ProjectState.InReview };
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

        var service = BuildService(github, taskExecutor, stateStore, timeProvider);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(150);

        await github.Received(1).MoveItemAsync(
            "item-1",
            ProjectState.InReview,
            ProjectState.Done,
            Arg.Any<CancellationToken>());

        await taskExecutor.DidNotReceive().RunAsync(Arg.Any<ProjectItem>(), Arg.Any<CancellationToken>());
        await taskExecutor.DidNotReceive().ResumeReviewAsync(Arg.Any<ProjectItem>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        cts.Cancel();
    }

    [Fact]
    public async Task No_Ready_item_on_tick_continues_loop()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = new InMemoryTaskStateStore();
        var timeProvider = new FakeTimeProvider();
        var workspaceMgr = Substitute.For<IWorkspaceManager>();
        var gitClient = Substitute.For<IGitClient>();
        var agentRunner = Substitute.For<IAgentRunner>();

        github.GetInFlightItemsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ProjectItem>());

        var readyItem = MakeItem("item-ready") with { State = ProjectState.Ready };
        var ws = MakeWorkspace("item-ready");
        var pr = MakePr();

        // First tick: null; second tick: real item; subsequent ticks: null
        var tickCount = 0;
        github.TryGetNextReadyItemAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                tickCount++;
                return tickCount == 2 ? readyItem : (ProjectItem?)null;
            });

        workspaceMgr.PrepareAsync(readyItem.ProjectItemId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ws);

        // Agent returns Completed with PR; PR is immediately merged+approved
        agentRunner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult(AgentRunOutcome.Completed, pr, 5, 10, null));

        github.GetPullRequestStatusAsync(pr.Number, Arg.Any<CancellationToken>())
            .Returns(new PullRequestStatus(pr.Number, PullRequestReviewState.Approved, true, true, "abc"));

        var taskExecutor = BuildTaskExecutor(github, workspaceMgr, gitClient, agentRunner, stateStore, timeProvider);

        using var cts = new CancellationTokenSource();
        var service = BuildService(github, taskExecutor, stateStore, timeProvider);
        var executeTask = service.StartAsync(cts.Token);
        await executeTask;

        await Task.Delay(50);

        // First tick (null item) — advance time
        timeProvider.Advance(TimeSpan.FromSeconds(11));
        await Task.Delay(50);

        // Second tick (real item) — agent runs
        timeProvider.Advance(TimeSpan.FromSeconds(11));
        await Task.Delay(50);

        // Review timer tick — marks PR as done
        timeProvider.Advance(TimeSpan.FromSeconds(11));

        await Task.Delay(200);

        // RunAsync was called exactly once (only on the second tick)
        await agentRunner.Received(1).RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>());

        cts.Cancel();
    }

    [Fact]
    public async Task Unhandled_exception_in_executor_releases_item_to_Ready_with_crash_comment()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = new InMemoryTaskStateStore();
        var timeProvider = new FakeTimeProvider();
        var workspaceMgr = Substitute.For<IWorkspaceManager>();
        var gitClient = Substitute.For<IGitClient>();
        var agentRunner = Substitute.For<IAgentRunner>();

        github.GetInFlightItemsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ProjectItem>());

        var readyItem = MakeItem("item-crash") with { State = ProjectState.Ready };
        github.TryGetNextReadyItemAsync(Arg.Any<CancellationToken>())
            .Returns(readyItem);

        // PrepareAsync throws
        workspaceMgr.PrepareAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var taskExecutor = BuildTaskExecutor(github, workspaceMgr, gitClient, agentRunner, stateStore, timeProvider);

        using var cts = new CancellationTokenSource();
        var service = BuildService(github, taskExecutor, stateStore, timeProvider);
        var executeTask = service.StartAsync(cts.Token);
        await executeTask;

        await Task.Delay(50);

        // Tick — triggers the executor which throws
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

        // State store should be cleared after the crash
        stateStore.Current.Should().BeNull();

        cts.Cancel();
    }

    [Fact]
    public async Task Startup_logs_warning_when_repository_does_not_exist()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = new InMemoryTaskStateStore();
        var timeProvider = new FakeTimeProvider();
        var logger = Substitute.For<ILogger<AgentLifecycleService>>();

        github.GetInFlightItemsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<ProjectItem>());
        github.GetReadyItemCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        github.TryGetNextReadyItemAsync(Arg.Any<CancellationToken>()).Returns((ProjectItem?)null);
        github.RepositoryExistsAsync(Arg.Any<CancellationToken>()).Returns(false);
        github.ProjectExistsAsync(Arg.Any<CancellationToken>()).Returns(true);

        var workspaceMgr = Substitute.For<IWorkspaceManager>();
        var gitClient = Substitute.For<IGitClient>();
        var agentRunner = Substitute.For<IAgentRunner>();
        var taskExecutor = BuildTaskExecutor(github, workspaceMgr, gitClient, agentRunner, stateStore, timeProvider);

        var service = BuildService(github, taskExecutor, stateStore, timeProvider, logger);

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
        var stateStore = new InMemoryTaskStateStore();
        var timeProvider = new FakeTimeProvider();
        var logger = Substitute.For<ILogger<AgentLifecycleService>>();

        github.GetInFlightItemsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<ProjectItem>());
        github.GetReadyItemCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        github.TryGetNextReadyItemAsync(Arg.Any<CancellationToken>()).Returns((ProjectItem?)null);
        github.RepositoryExistsAsync(Arg.Any<CancellationToken>()).Returns(true);
        github.ProjectExistsAsync(Arg.Any<CancellationToken>()).Returns(false);

        var workspaceMgr = Substitute.For<IWorkspaceManager>();
        var gitClient = Substitute.For<IGitClient>();
        var agentRunner = Substitute.For<IAgentRunner>();
        var taskExecutor = BuildTaskExecutor(github, workspaceMgr, gitClient, agentRunner, stateStore, timeProvider);

        var service = BuildService(github, taskExecutor, stateStore, timeProvider, logger);

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
        var stateStore = new InMemoryTaskStateStore();
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

        var workspaceMgr = Substitute.For<IWorkspaceManager>();
        var gitClient = Substitute.For<IGitClient>();
        var agentRunner = Substitute.For<IAgentRunner>();
        var taskExecutor = BuildTaskExecutor(github, workspaceMgr, gitClient, agentRunner, stateStore, timeProvider);

        var service = BuildService(github, taskExecutor, stateStore, timeProvider, logger, gitHubOptions);

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
        var stateStore = new InMemoryTaskStateStore();
        var timeProvider = new FakeTimeProvider();
        var logger = Substitute.For<ILogger<AgentLifecycleService>>();

        github.GetInFlightItemsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<ProjectItem>());
        github.GetReadyItemCountAsync(Arg.Any<CancellationToken>()).Returns(5);
        github.TryGetNextReadyItemAsync(Arg.Any<CancellationToken>()).Returns((ProjectItem?)null);

        var workspaceMgr = Substitute.For<IWorkspaceManager>();
        var gitClient = Substitute.For<IGitClient>();
        var agentRunner = Substitute.For<IAgentRunner>();
        var taskExecutor = BuildTaskExecutor(github, workspaceMgr, gitClient, agentRunner, stateStore, timeProvider);

        var service = BuildService(github, taskExecutor, stateStore, timeProvider, logger);

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
    public async Task No_Ready_item_on_tick_logs_Information_waiting_message()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var stateStore = new InMemoryTaskStateStore();
        var timeProvider = new FakeTimeProvider();
        var logger = Substitute.For<ILogger<AgentLifecycleService>>();

        github.GetInFlightItemsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<ProjectItem>());
        github.GetReadyItemCountAsync(Arg.Any<CancellationToken>()).Returns(0);
        github.TryGetNextReadyItemAsync(Arg.Any<CancellationToken>()).Returns((ProjectItem?)null);

        var workspaceMgr = Substitute.For<IWorkspaceManager>();
        var gitClient = Substitute.For<IGitClient>();
        var agentRunner = Substitute.For<IAgentRunner>();
        var taskExecutor = BuildTaskExecutor(github, workspaceMgr, gitClient, agentRunner, stateStore, timeProvider);

        var service = BuildService(github, taskExecutor, stateStore, timeProvider, logger);

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

        cts.Cancel();
    }

}

file static class OptionsFactory
{
    public static IOptions<T> Create<T>(T value) where T : class =>
        Microsoft.Extensions.Options.Options.Create(value);
}
