using Agent.Workspace;
using Dapr.Workflow;
using DeveloperAgent.Configuration;
using DeveloperAgent.GitHub;
using DeveloperAgent.Resolution;
using DeveloperAgent.Workflow;
using DeveloperAgent.Workflow.Activities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Tests.Workflow;

/// <summary>
/// Unit tests for <see cref="CheckAlreadyResolvedActivity"/> — the post-branch "already implemented?"
/// gate. Verifies the disabled no-op, the not-resolved pass-through, and the resolved path
/// (read comments → comment → move InProgress→Backlog → release workspace).
/// </summary>
public sealed class CheckAlreadyResolvedActivityTests
{
    private static ResolutionCheckActivityInput MakeInput(string itemId = "PVTI_abc") =>
        new(
            ProjectItemId: itemId,
            ContentNodeId: $"I_node_{itemId}",
            Title: "Step-99 add the WidgetService",
            BodyMarkdown: "Implement WidgetService.",
            BranchName: "agent/step-99",
            WorkspacePath: @"C:\ws\step-99",
            DefaultBranch: "main");

    private static CheckAlreadyResolvedActivity Build(
        IGitHubProjectService github,
        IResolutionChecker checker,
        IWorkspaceManager workspaceManager,
        bool enabled) =>
        new(
            NullLogger<CheckAlreadyResolvedActivity>.Instance,
            github,
            checker,
            workspaceManager,
            Options.Create(new ResolutionCheckOptions { Enabled = enabled }));

    [Fact]
    public async Task When_disabled_returns_not_resolved_without_calling_checker_or_github()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var checker = Substitute.For<IResolutionChecker>();
        var ws = Substitute.For<IWorkspaceManager>();
        var activity = Build(github, checker, ws, enabled: false);

        var result = await activity.RunAsync(new FakeResolutionActivityContext(), MakeInput());

        result.IsAlreadyResolved.Should().BeFalse();
        await checker.DidNotReceive().EvaluateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await github.DidNotReceive().GetItemCommentsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await github.DidNotReceive().AddItemCommentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await github.DidNotReceive().MoveItemAsync(Arg.Any<string>(), Arg.Any<ProjectState>(), Arg.Any<ProjectState>(), Arg.Any<CancellationToken>());
        await ws.DidNotReceive().ReleaseAsync(Arg.Any<TaskWorkspace>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task When_enabled_and_not_resolved_proceeds_without_commenting_moving_or_releasing()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var checker = Substitute.For<IResolutionChecker>();
        var ws = Substitute.For<IWorkspaceManager>();
        checker.EvaluateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ResolutionVerdict(false, "Not implemented yet."));
        var activity = Build(github, checker, ws, enabled: true);

        var result = await activity.RunAsync(new FakeResolutionActivityContext(), MakeInput());

        result.IsAlreadyResolved.Should().BeFalse();
        await github.DidNotReceive().AddItemCommentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await github.DidNotReceive().MoveItemAsync(Arg.Any<string>(), Arg.Any<ProjectState>(), Arg.Any<ProjectState>(), Arg.Any<CancellationToken>());
        await ws.DidNotReceive().ReleaseAsync(Arg.Any<TaskWorkspace>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Passes_the_items_existing_comments_into_the_checker()
    {
        // "Read item text and all comments, consider whether it was really developed." The activity must
        // fetch the item's comments and hand them to the checker alongside title/body/workspace.
        var github = Substitute.For<IGitHubProjectService>();
        var checker = Substitute.For<IResolutionChecker>();
        var ws = Substitute.For<IWorkspaceManager>();
        github.GetItemCommentsAsync("I_node_PVTI_abc", Arg.Any<CancellationToken>())
            .Returns("> **@alice**: I think this was already done in #123");
        checker.EvaluateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ResolutionVerdict(false, "uncertain"));
        var activity = Build(github, checker, ws, enabled: true);

        await activity.RunAsync(new FakeResolutionActivityContext(), MakeInput());

        await checker.Received(1).EvaluateAsync(
            "Step-99 add the WidgetService",
            "Implement WidgetService.",
            Arg.Is<string>(c => c.Contains("already done in #123")),
            @"C:\ws\step-99",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failing_comment_fetch_does_not_crash_the_gate_it_proceeds_with_empty_comments()
    {
        // The gate's whole value proposition is "optional, never block work." A persistent GitHub read
        // failure on the comment fetch must degrade to "proceed" (empty comments), not throw and leave
        // the item stuck in InProgress after Dapr exhausts retries.
        var github = Substitute.For<IGitHubProjectService>();
        var checker = Substitute.For<IResolutionChecker>();
        var ws = Substitute.For<IWorkspaceManager>();
        github.GetItemCommentsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new InvalidOperationException("GitHub down"));
        checker.EvaluateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ResolutionVerdict(false, "uncertain"));
        var activity = Build(github, checker, ws, enabled: true);

        var result = await activity.RunAsync(new FakeResolutionActivityContext(), MakeInput());

        result.IsAlreadyResolved.Should().BeFalse();
        await checker.Received(1).EvaluateAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<string>(c => c == string.Empty),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task When_enabled_and_resolved_comments_moves_to_Backlog_and_releases_workspace()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var checker = Substitute.For<IResolutionChecker>();
        var ws = Substitute.For<IWorkspaceManager>();
        checker.EvaluateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ResolutionVerdict(true, "WidgetService already exists in src/Services."));
        var activity = Build(github, checker, ws, enabled: true);

        var input = MakeInput("PVTI_done");
        var result = await activity.RunAsync(new FakeResolutionActivityContext(), input);

        result.IsAlreadyResolved.Should().BeTrue();

        await github.Received(1).AddItemCommentAsync(
            "I_node_PVTI_done",
            Arg.Is<string>(c => c.Contains("WidgetService already exists in src/Services.") && c.Contains("Backlog")),
            Arg.Any<CancellationToken>());

        await github.Received(1).MoveItemAsync(
            "PVTI_done", ProjectState.InProgress, ProjectState.Backlog, Arg.Any<CancellationToken>());

        await ws.Received(1).ReleaseAsync(
            Arg.Is<TaskWorkspace>(w => w.ProjectItemId == "PVTI_done" && w.RepoRoot == @"C:\ws\step-99"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resolved_path_comments_before_it_moves_the_item()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var checker = Substitute.For<IResolutionChecker>();
        var ws = Substitute.For<IWorkspaceManager>();
        checker.EvaluateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ResolutionVerdict(true, "Already done."));
        var activity = Build(github, checker, ws, enabled: true);

        await activity.RunAsync(new FakeResolutionActivityContext(), MakeInput());

        Received.InOrder(() =>
        {
            github.AddItemCommentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            github.MoveItemAsync(Arg.Any<string>(), ProjectState.InProgress, ProjectState.Backlog, Arg.Any<CancellationToken>());
        });
    }
}

/// <summary>Minimal concrete <see cref="WorkflowActivityContext"/> for activity unit tests.</summary>
file sealed class FakeResolutionActivityContext : WorkflowActivityContext
{
    public override Dapr.Workflow.Abstractions.TaskIdentifier Identifier => "fake-task-name";
    public override string InstanceId => "fake-instance-id";
    public override string TaskExecutionKey => "fake-task-key";
}
