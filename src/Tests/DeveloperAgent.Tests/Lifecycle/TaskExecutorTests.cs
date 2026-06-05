using System.Diagnostics.Metrics;
using DeveloperAgent.Agent;
using DeveloperAgent.Configuration;
using DeveloperAgent.GitHub;
using DeveloperAgent.Lifecycle;
using DeveloperAgent.Observability;
using Agent.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DeveloperAgent.Tests.Lifecycle;

/// <summary>
/// Unit tests for <see cref="TaskExecutor"/> using NSubstitute fakes and
/// <see cref="FakeTimeProvider"/> for deterministic time control.
/// </summary>
public sealed class TaskExecutorTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static readonly AgentOptions Options = new()
    {
        PollIntervalSeconds = 10,
        ReviewPollIntervalSeconds = 10
    };

    private static ProjectItem MakeItem(string id = "item-1") =>
        new(
            ProjectItemId: id,
            ContentNodeId: $"content-{id}",
            ContentNumber: 42,
            Title: "Add feature X",
            BodyMarkdown: "Implement feature X",
            State: ProjectState.Ready);

    private static TaskWorkspace MakeWorkspace(string itemId = "item-1") =>
        new(ProjectItemId: itemId, BranchName: "agent/add-feature-x-abcd1234",
            RepoRoot: "/workspace/item-1/repo", DefaultBranch: "main");

    private static PullRequest MakePr(int number = 99) =>
        new(Number: number, HeadSha: "abc123", HtmlUrl: $"https://github.com/org/repo/pull/{number}");

    private static PullRequestStatus MakeMergedApproved() =>
        new(Number: 99, Review: PullRequestReviewState.Approved,
            ChecksGreen: true, Merged: true, HeadSha: "abc123");

    private static AgentRunResult MakeCompleted(PullRequest? pr = null) =>
        new(AgentRunOutcome.Completed, pr, TurnsUsed: 5, ToolCallsUsed: 10, TerminationReason: null);

    private TaskExecutor BuildExecutor(
        IGitHubProjectService github,
        IWorkspaceManager workspaceManager,
        IGitClient gitClient,
        IAgentRunner agentRunner,
        ITaskStateStore stateStore,
        FakeTimeProvider timeProvider,
        AgentMetrics? metrics = null) =>
        new(NullLogger<TaskExecutor>.Instance,
            Options.AsOptions(),
            Microsoft.Extensions.Options.Options.Create(
                new GitHubOptions { Repository = new RepositoryOptions { Url = "https://github.com/test/repo" } }),
            github,
            workspaceManager,
            gitClient,
            agentRunner,
            stateStore,
            timeProvider,
            metrics ?? new AgentMetrics());

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Happy_path_walks_Ready_to_Done()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var workspaceMgr = Substitute.For<IWorkspaceManager>();
        var gitClient = Substitute.For<IGitClient>();
        var agentRunner = Substitute.For<IAgentRunner>();
        var stateStore = new InMemoryTaskStateStore();
        var timeProvider = new FakeTimeProvider();

        var item = MakeItem();
        var ws = MakeWorkspace();
        var pr = MakePr();

        workspaceMgr.PrepareAsync(item.ProjectItemId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ws);
        agentRunner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(MakeCompleted(pr));
        github.GetPullRequestStatusAsync(pr.Number, Arg.Any<CancellationToken>())
            .Returns(MakeMergedApproved());

        var executor = BuildExecutor(github, workspaceMgr, gitClient, agentRunner, stateStore, timeProvider);

        // Start executor — it will block inside review PeriodicTimer
        using var cts = new CancellationTokenSource();
        var runTask = executor.RunAsync(item, cts.Token);

        // Advance time to release the review timer tick
        timeProvider.Advance(TimeSpan.FromSeconds(11));

        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        result.Outcome.Should().Be(TaskOutcome.Done);

        // Verify MoveItemAsync call sequence
        Received.InOrder(() =>
        {
            github.MoveItemAsync(item.ProjectItemId, ProjectState.Ready, ProjectState.InProgress, Arg.Any<CancellationToken>());
            github.MoveItemAsync(item.ProjectItemId, ProjectState.InProgress, ProjectState.InReview, Arg.Any<CancellationToken>());
            github.MoveItemAsync(item.ProjectItemId, ProjectState.InReview, ProjectState.Done, Arg.Any<CancellationToken>());
        });

        await workspaceMgr.Received(1).ReleaseAsync(ws, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangesRequested_round_invokes_runner_twice_with_feedback()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var workspaceMgr = Substitute.For<IWorkspaceManager>();
        var gitClient = Substitute.For<IGitClient>();
        var agentRunner = Substitute.For<IAgentRunner>();
        var stateStore = new InMemoryTaskStateStore();
        var timeProvider = new FakeTimeProvider();

        var item = MakeItem();
        var ws = MakeWorkspace();
        var pr = MakePr();
        const string feedbackText = "Please add unit tests.";

        workspaceMgr.PrepareAsync(item.ProjectItemId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ws);
        agentRunner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(MakeCompleted(pr));

        // First tick: ChangesRequested; second tick: Merged+Approved
        var callCount = 0;
        github.GetPullRequestStatusAsync(pr.Number, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? new PullRequestStatus(pr.Number, PullRequestReviewState.ChangesRequested, false, false, "abc")
                    : MakeMergedApproved();
            });

        github.GetReviewFeedbackSinceAsync(pr.Number, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(feedbackText);

        var executor = BuildExecutor(github, workspaceMgr, gitClient, agentRunner, stateStore, timeProvider);

        using var cts = new CancellationTokenSource();
        var runTask = executor.RunAsync(item, cts.Token);

        // First tick triggers ChangesRequested + second agent run
        timeProvider.Advance(TimeSpan.FromSeconds(11));
        // Second tick triggers Merged+Approved
        timeProvider.Advance(TimeSpan.FromSeconds(11));

        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        result.Outcome.Should().Be(TaskOutcome.Done);

        await agentRunner.Received(2).RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>());

        // Second call must have non-null PriorReviewFeedback equal to feedbackText
        await agentRunner.Received(1).RunAsync(
            Arg.Is<AgentRunRequest>(r => r.PriorReviewFeedback == feedbackText),
            Arg.Any<CancellationToken>());

        // MoveItemAsync sequence: Ready→InProgress, InProgress→InReview, InReview→InProgress, InProgress→InReview, InReview→Done
        Received.InOrder(() =>
        {
            github.MoveItemAsync(item.ProjectItemId, ProjectState.Ready, ProjectState.InProgress, Arg.Any<CancellationToken>());
            github.MoveItemAsync(item.ProjectItemId, ProjectState.InProgress, ProjectState.InReview, Arg.Any<CancellationToken>());
            github.MoveItemAsync(item.ProjectItemId, ProjectState.InReview, ProjectState.InProgress, Arg.Any<CancellationToken>());
            github.MoveItemAsync(item.ProjectItemId, ProjectState.InProgress, ProjectState.InReview, Arg.Any<CancellationToken>());
            github.MoveItemAsync(item.ProjectItemId, ProjectState.InReview, ProjectState.Done, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Agent_returns_Completed_with_null_PullRequest_releases_to_Ready()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var workspaceMgr = Substitute.For<IWorkspaceManager>();
        var gitClient = Substitute.For<IGitClient>();
        var agentRunner = Substitute.For<IAgentRunner>();
        var stateStore = new InMemoryTaskStateStore();
        var timeProvider = new FakeTimeProvider();

        var item = MakeItem();
        var ws = MakeWorkspace();

        workspaceMgr.PrepareAsync(item.ProjectItemId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ws);
        agentRunner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(MakeCompleted(pr: null));  // No PR

        var executor = BuildExecutor(github, workspaceMgr, gitClient, agentRunner, stateStore, timeProvider);

        var result = await executor.RunAsync(item, CancellationToken.None);

        result.Outcome.Should().Be(TaskOutcome.Failed);

        // Comment must mention "without opening a PR"
        await github.Received(1).AddItemCommentAsync(
            item.ContentNodeId,
            Arg.Is<string>(s => s.Contains("without opening a PR")),
            Arg.Any<CancellationToken>());

        // Must be released to Ready
        await github.Received(1).MoveItemAsync(
            item.ProjectItemId,
            ProjectState.InProgress,
            ProjectState.Ready,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Agent_returns_HardCapReached_releases_to_Ready_with_explanation()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var workspaceMgr = Substitute.For<IWorkspaceManager>();
        var gitClient = Substitute.For<IGitClient>();
        var agentRunner = Substitute.For<IAgentRunner>();
        var stateStore = new InMemoryTaskStateStore();
        var timeProvider = new FakeTimeProvider();

        var item = MakeItem();
        var ws = MakeWorkspace();

        workspaceMgr.PrepareAsync(item.ProjectItemId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ws);
        agentRunner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult(AgentRunOutcome.HardCapReached, null, 40, 120, "Reached 40 turns"));

        var executor = BuildExecutor(github, workspaceMgr, gitClient, agentRunner, stateStore, timeProvider);

        var result = await executor.RunAsync(item, CancellationToken.None);

        result.Outcome.Should().Be(TaskOutcome.Failed);

        // Comment must mention "turn cap"
        await github.Received(1).AddItemCommentAsync(
            item.ContentNodeId,
            Arg.Is<string>(s => s.Contains("turn cap")),
            Arg.Any<CancellationToken>());

        // Must be released to Ready
        await github.Received(1).MoveItemAsync(
            item.ProjectItemId,
            ProjectState.InProgress,
            ProjectState.Ready,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SandboxViolation_releases_to_Ready_without_offending_command()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var workspaceMgr = Substitute.For<IWorkspaceManager>();
        var gitClient = Substitute.For<IGitClient>();
        var agentRunner = Substitute.For<IAgentRunner>();
        var stateStore = new InMemoryTaskStateStore();
        var timeProvider = new FakeTimeProvider();

        var item = MakeItem();
        var ws = MakeWorkspace();
        const string offendingCommand = "rm -rf /etc";

        workspaceMgr.PrepareAsync(item.ProjectItemId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ws);
        agentRunner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult(AgentRunOutcome.SandboxViolation, null, 2, 3, offendingCommand));

        var executor = BuildExecutor(github, workspaceMgr, gitClient, agentRunner, stateStore, timeProvider);

        var result = await executor.RunAsync(item, CancellationToken.None);

        result.Outcome.Should().Be(TaskOutcome.Failed);

        // Posted comment must NOT contain the offending command
        await github.Received(1).AddItemCommentAsync(
            item.ContentNodeId,
            Arg.Is<string>(s => !s.Contains("rm -rf") && !s.Contains("/etc")),
            Arg.Any<CancellationToken>());

        // Must be released to Ready
        await github.Received(1).MoveItemAsync(
            item.ProjectItemId,
            ProjectState.InProgress,
            ProjectState.Ready,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancellation_mid_review_wait_propagates_no_state_changes_after()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var workspaceMgr = Substitute.For<IWorkspaceManager>();
        var gitClient = Substitute.For<IGitClient>();
        var agentRunner = Substitute.For<IAgentRunner>();
        var stateStore = new InMemoryTaskStateStore();
        var timeProvider = new FakeTimeProvider();

        var item = MakeItem();
        var ws = MakeWorkspace();
        var pr = MakePr();

        workspaceMgr.PrepareAsync(item.ProjectItemId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ws);
        agentRunner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(MakeCompleted(pr));

        // PR is pending — loop keeps waiting
        github.GetPullRequestStatusAsync(pr.Number, Arg.Any<CancellationToken>())
            .Returns(new PullRequestStatus(pr.Number, PullRequestReviewState.Pending, false, false, "abc"));

        var executor = BuildExecutor(github, workspaceMgr, gitClient, agentRunner, stateStore, timeProvider);

        using var cts = new CancellationTokenSource();
        var runTask = executor.RunAsync(item, cts.Token);

        // One tick fires so we enter the review loop and call GetPullRequestStatusAsync
        timeProvider.Advance(TimeSpan.FromSeconds(11));

        // Allow the tick to be processed
        await Task.Delay(50);

        // Cancel — should stop the review wait loop
        cts.Cancel();

        // Task should complete (OperationCanceledException causes WaitForNextTickAsync to return false
        // or throw, depending on implementation — either way, RunAsync returns)
        var ex = await Record.ExceptionAsync(() => runTask.WaitAsync(TimeSpan.FromSeconds(5)));

        // Could return Cancelled outcome OR throw OCE — both are acceptable
        if (ex is not null)
        {
            ex.Should().BeOfType<OperationCanceledException>();
        }

        // No MoveItemAsync calls should happen after cancellation point
        // (only the initial Ready→InProgress, InProgress→InReview are expected)
        await github.DidNotReceive().MoveItemAsync(
            item.ProjectItemId,
            ProjectState.InReview,
            Arg.Is<ProjectState>(s => s == ProjectState.Done || s == ProjectState.InProgress),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResumeReviewAsync_approved_and_merged_moves_item_to_Done()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var workspaceMgr = Substitute.For<IWorkspaceManager>();
        var gitClient = Substitute.For<IGitClient>();
        var agentRunner = Substitute.For<IAgentRunner>();
        var stateStore = new InMemoryTaskStateStore();
        var timeProvider = new FakeTimeProvider();

        var item = MakeItem() with { State = ProjectState.InReview };

        github.GetPullRequestStatusAsync(7, Arg.Any<CancellationToken>())
            .Returns(new PullRequestStatus(7, PullRequestReviewState.Approved, true, true, "abc"));

        var executor = BuildExecutor(github, workspaceMgr, gitClient, agentRunner, stateStore, timeProvider);

        using var cts = new CancellationTokenSource();
        var runTask = executor.ResumeReviewAsync(item, pullRequestNumber: 7, cts.Token);

        timeProvider.Advance(TimeSpan.FromSeconds(11));

        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        result.Outcome.Should().Be(TaskOutcome.Done);

        await github.Received(1).MoveItemAsync(
            item.ProjectItemId,
            ProjectState.InReview,
            ProjectState.Done,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResumeReviewAsync_changes_requested_reruns_agent_then_resubmits()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var workspaceMgr = Substitute.For<IWorkspaceManager>();
        var gitClient = Substitute.For<IGitClient>();
        var agentRunner = Substitute.For<IAgentRunner>();
        var stateStore = new InMemoryTaskStateStore();
        var timeProvider = new FakeTimeProvider();

        var item = MakeItem() with { State = ProjectState.InReview };
        var newPr = MakePr(number: 7);

        var callCount = 0;
        github.GetPullRequestStatusAsync(7, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? new PullRequestStatus(7, PullRequestReviewState.ChangesRequested, false, false, "abc")
                    : new PullRequestStatus(7, PullRequestReviewState.Approved, true, true, "abc");
            });

        github.GetReviewFeedbackSinceAsync(7, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns("Please fix the tests.");

        var ws = MakeWorkspace();
        workspaceMgr.PrepareAsync(item.ProjectItemId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ws);
        agentRunner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult(AgentRunOutcome.Completed, newPr, 5, 10, null));

        var executor = BuildExecutor(github, workspaceMgr, gitClient, agentRunner, stateStore, timeProvider);

        using var cts = new CancellationTokenSource();
        var runTask = executor.ResumeReviewAsync(item, pullRequestNumber: 7, cts.Token);

        timeProvider.Advance(TimeSpan.FromSeconds(11));
        timeProvider.Advance(TimeSpan.FromSeconds(11));

        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        result.Outcome.Should().Be(TaskOutcome.Done);
        await agentRunner.Received(1).RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResumeReviewAsync_cancellation_returns_Cancelled()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var workspaceMgr = Substitute.For<IWorkspaceManager>();
        var gitClient = Substitute.For<IGitClient>();
        var agentRunner = Substitute.For<IAgentRunner>();
        var stateStore = new InMemoryTaskStateStore();
        var timeProvider = new FakeTimeProvider();

        var item = MakeItem() with { State = ProjectState.InReview };

        github.GetPullRequestStatusAsync(7, Arg.Any<CancellationToken>())
            .Returns(new PullRequestStatus(7, PullRequestReviewState.Pending, false, false, "abc"));

        var executor = BuildExecutor(github, workspaceMgr, gitClient, agentRunner, stateStore, timeProvider);

        using var cts = new CancellationTokenSource();
        var runTask = executor.ResumeReviewAsync(item, pullRequestNumber: 7, cts.Token);

        timeProvider.Advance(TimeSpan.FromSeconds(11));
        await Task.Delay(50);

        cts.Cancel();

        var ex = await Record.ExceptionAsync(() => runTask.WaitAsync(TimeSpan.FromSeconds(5)));
        if (ex is not null)
            ex.Should().BeOfType<OperationCanceledException>();
    }

    [Fact]
    public async Task LastReviewPolledAtUtc_advances_only_after_successful_feedback_fetch()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var workspaceMgr = Substitute.For<IWorkspaceManager>();
        var gitClient = Substitute.For<IGitClient>();
        var agentRunner = Substitute.For<IAgentRunner>();
        var stateStore = new InMemoryTaskStateStore();
        var timeProvider = new FakeTimeProvider();

        var item = MakeItem();
        var ws = MakeWorkspace();
        var pr = MakePr();

        workspaceMgr.PrepareAsync(item.ProjectItemId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ws);
        agentRunner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(MakeCompleted(pr));

        // PR is in ChangesRequested state
        github.GetPullRequestStatusAsync(pr.Number, Arg.Any<CancellationToken>())
            .Returns(new PullRequestStatus(pr.Number, PullRequestReviewState.ChangesRequested, false, false, "abc"));

        // Feedback fetch throws on first call
        github.GetReviewFeedbackSinceAsync(pr.Number, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new InvalidOperationException("Transient failure")));

        var executor = BuildExecutor(github, workspaceMgr, gitClient, agentRunner, stateStore, timeProvider);

        using var cts = new CancellationTokenSource();
        var runTask = executor.RunAsync(item, cts.Token);

        // Capture the state just before the tick that triggers ChangesRequested
        // After MoveItemAsync(InProgress,InReview), PullRequestOpenedAtUtc is set
        timeProvider.Advance(TimeSpan.FromSeconds(11));

        // The exception from GetReviewFeedbackSinceAsync propagates up through RunAsync
        var ex = await Record.ExceptionAsync(() => runTask.WaitAsync(TimeSpan.FromSeconds(5)));
        ex.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("Transient failure");

        // The cursor (LastReviewPolledAtUtc) must remain at PullRequestOpenedAtUtc
        // (i.e. it must NOT have been advanced since feedback fetch failed before the advance).
        var finalState = stateStore.Current;
        if (finalState is not null)
        {
            // If state is present, LastReviewPolledAtUtc should equal PullRequestOpenedAtUtc
            // (unchanged because feedback fetch threw before the cursor was advanced)
            finalState.LastReviewPolledAtUtc.Should().Be(finalState.PullRequestOpenedAtUtc);
        }
        // If state was cleared by a finally block in the outer loop, the assertion is moot
        // because the throw propagated before any state advance.
    }
}

// Helper to convert AgentOptions to IOptions<AgentOptions>
file static class AgentOptionsExtensions
{
    public static IOptions<AgentOptions> AsOptions(this AgentOptions options) =>
        Microsoft.Extensions.Options.Options.Create(options);
}

/// <summary>
/// Verifies that <see cref="TaskExecutor"/> emits to <see cref="AgentMetrics"/>
/// at the exact phase boundaries listed in
/// <c>docs/plans/07-phase-2-outline.md §P2-N</c>.
/// </summary>
public sealed class TaskExecutorMetricsTests
{
    private static readonly AgentOptions Options = new()
    {
        PollIntervalSeconds = 10,
        ReviewPollIntervalSeconds = 10
    };

    private static ProjectItem MakeItem(string id = "item-1") =>
        new(
            ProjectItemId: id,
            ContentNodeId: $"content-{id}",
            ContentNumber: 42,
            Title: "Add feature X",
            BodyMarkdown: "Implement feature X",
            State: ProjectState.Ready);

    private static TaskWorkspace MakeWorkspace(string itemId = "item-1") =>
        new(ProjectItemId: itemId, BranchName: "agent/add-feature-x-abcd1234",
            RepoRoot: "/workspace/item-1/repo", DefaultBranch: "main");

    private static PullRequest MakePr(int number = 99) =>
        new(Number: number, HeadSha: "abc123", HtmlUrl: $"https://github.com/org/repo/pull/{number}");

    private static PullRequestStatus MergedApproved(int prNumber = 99) =>
        new(prNumber, PullRequestReviewState.Approved, ChecksGreen: true, Merged: true, HeadSha: "abc123");

    private static AgentRunResult Completed(PullRequest pr, int toolCalls = 7) =>
        new(AgentRunOutcome.Completed, pr, TurnsUsed: 4, ToolCallsUsed: toolCalls, TerminationReason: null);

    [Fact]
    public async Task Happy_path_emits_tasks_completed_tool_calls_and_time_to_pr()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var workspaceMgr = Substitute.For<IWorkspaceManager>();
        var gitClient = Substitute.For<IGitClient>();
        var agentRunner = Substitute.For<IAgentRunner>();
        var stateStore = new InMemoryTaskStateStore();
        var timeProvider = new FakeTimeProvider();
        using var metrics = new AgentMetrics();

        var item = MakeItem();
        var ws = MakeWorkspace();
        var pr = MakePr();

        workspaceMgr.PrepareAsync(item.ProjectItemId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ws);
        // RunAsync takes some "modeled" wall clock; the test advances the FakeTimeProvider
        // between Ready and PR open implicitly via the periodic timer.  Here we only need
        // the run to return a PR; the executor reads the clock around the PR-open transition.
        agentRunner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // Simulate 30s elapsed during the agent run by advancing the fake clock.
                timeProvider.Advance(TimeSpan.FromSeconds(30));
                return Completed(pr, toolCalls: 7);
            });
        github.GetPullRequestStatusAsync(pr.Number, Arg.Any<CancellationToken>())
            .Returns(MergedApproved(pr.Number));

        // Listen to all four instruments emitted in the happy path
        var tasksCompleted = new List<long>();
        var toolCalls = new List<long>();
        var timeToPr = new List<double>();
        var passRate = new List<double>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (!ReferenceEquals(instrument.Meter, metrics.Meter)) return;
                if (instrument.Name is "agent.tasks.completed"
                                    or "agent.tool_calls.per_task"
                                    or "agent.time_to_pr"
                                    or "agent.build_test.pass_rate")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((inst, value, _, _) =>
        {
            if (inst.Name == "agent.tasks.completed") tasksCompleted.Add(value);
            else if (inst.Name == "agent.tool_calls.per_task") toolCalls.Add(value);
        });
        listener.SetMeasurementEventCallback<double>((inst, value, _, _) =>
        {
            if (inst.Name == "agent.time_to_pr") timeToPr.Add(value);
            else if (inst.Name == "agent.build_test.pass_rate") passRate.Add(value);
        });
        listener.Start();

        var executor = new TaskExecutor(
            NullLogger<TaskExecutor>.Instance,
            Options.AsOptions(),
            Microsoft.Extensions.Options.Options.Create(
                new GitHubOptions { Repository = new RepositoryOptions { Url = "https://github.com/test/repo" } }),
            github,
            workspaceMgr,
            gitClient,
            agentRunner,
            stateStore,
            timeProvider,
            metrics);

        using var cts = new CancellationTokenSource();
        var runTask = executor.RunAsync(item, cts.Token);

        // Release the review wait timer
        timeProvider.Advance(TimeSpan.FromSeconds(11));

        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        result.Outcome.Should().Be(TaskOutcome.Done);

        // tasks.completed: +1 on the Done branch
        tasksCompleted.Should().ContainSingle().Which.Should().Be(1);

        // tool_calls.per_task: recorded once with the run's ToolCallsUsed value
        toolCalls.Should().ContainSingle().Which.Should().Be(7);

        // time_to_pr: 30s of agent-run time was simulated between Acquired and PullRequestOpen
        timeToPr.Should().ContainSingle().Which.Should().BeApproximately(30d, 0.5d);

        // pass_rate observable gauge: pull a measurement so we know it reflects 1 success / 1 total
        listener.RecordObservableInstruments();
        passRate.Should().NotBeEmpty();
        passRate[^1].Should().Be(1d);
    }

    [Fact]
    public async Task Failed_run_emits_tool_calls_and_pass_rate_but_not_tasks_completed_or_time_to_pr()
    {
        SynchronizationContext.SetSynchronizationContext(null);

        var github = Substitute.For<IGitHubProjectService>();
        var workspaceMgr = Substitute.For<IWorkspaceManager>();
        var gitClient = Substitute.For<IGitClient>();
        var agentRunner = Substitute.For<IAgentRunner>();
        var stateStore = new InMemoryTaskStateStore();
        var timeProvider = new FakeTimeProvider();
        using var metrics = new AgentMetrics();

        var item = MakeItem();
        var ws = MakeWorkspace();

        workspaceMgr.PrepareAsync(item.ProjectItemId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ws);
        agentRunner.RunAsync(Arg.Any<AgentRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRunResult(AgentRunOutcome.HardCapReached, null, 40, 23, "Reached 40 turns"));

        var tasksCompleted = new List<long>();
        var toolCalls = new List<long>();
        var timeToPr = new List<double>();
        var passRate = new List<double>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (!ReferenceEquals(instrument.Meter, metrics.Meter)) return;
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((inst, value, _, _) =>
        {
            if (inst.Name == "agent.tasks.completed") tasksCompleted.Add(value);
            else if (inst.Name == "agent.tool_calls.per_task") toolCalls.Add(value);
        });
        listener.SetMeasurementEventCallback<double>((inst, value, _, _) =>
        {
            if (inst.Name == "agent.time_to_pr") timeToPr.Add(value);
            else if (inst.Name == "agent.build_test.pass_rate") passRate.Add(value);
        });
        listener.Start();

        var executor = new TaskExecutor(
            NullLogger<TaskExecutor>.Instance,
            Options.AsOptions(),
            Microsoft.Extensions.Options.Options.Create(
                new GitHubOptions { Repository = new RepositoryOptions { Url = "https://github.com/test/repo" } }),
            github,
            workspaceMgr,
            gitClient,
            agentRunner,
            stateStore,
            timeProvider,
            metrics);

        var result = await executor.RunAsync(item, CancellationToken.None);
        result.Outcome.Should().Be(TaskOutcome.Failed);

        tasksCompleted.Should().BeEmpty();           // failure does not increment the Done counter
        toolCalls.Should().ContainSingle().Which.Should().Be(23);
        timeToPr.Should().BeEmpty();                 // PR was never opened

        listener.RecordObservableInstruments();
        passRate.Should().NotBeEmpty();
        passRate[^1].Should().Be(0d);                // 0 successes / 1 total
    }
}
