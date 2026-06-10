using Dapr.Workflow;
using DeveloperAgent.Configuration;
using DeveloperAgent.GitHub;
using DeveloperAgent.Triage;
using DeveloperAgent.Workflow;
using DeveloperAgent.Workflow.Activities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Tests.Workflow;

/// <summary>
/// Unit tests for <see cref="TriageActivity"/> — the relevance gate. Verifies the disabled no-op,
/// the relevant pass-through, and the rejection path (comment + move Ready→Backlog).
/// </summary>
public sealed class TriageActivityTests
{
    private static TriageActivityInput MakeInput(string itemId = "PVTI_abc") =>
        new(
            ProjectItemId: itemId,
            ContentNodeId: $"I_node_{itemId}",
            ContentNumber: 42,
            Title: "Some item",
            BodyMarkdown: "body");

    private static TriageActivity Build(
        IGitHubProjectService github,
        ITriageService triage,
        bool enabled) =>
        new(
            NullLogger<TriageActivity>.Instance,
            github,
            triage,
            Options.Create(new TriageOptions { Enabled = enabled, RepoScope = "scope" }));

    [Fact]
    public async Task When_disabled_returns_relevant_without_calling_the_triage_service()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var triage = Substitute.For<ITriageService>();
        var activity = Build(github, triage, enabled: false);

        var result = await activity.RunAsync(new FakeTriageActivityContext(), MakeInput());

        result.IsRelevant.Should().BeTrue();
        await triage.DidNotReceive().EvaluateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await github.DidNotReceive().MoveItemAsync(Arg.Any<string>(), Arg.Any<ProjectState>(), Arg.Any<ProjectState>(), Arg.Any<CancellationToken>());
        await github.DidNotReceive().AddItemCommentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task When_enabled_and_relevant_proceeds_without_commenting_or_moving()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var triage = Substitute.For<ITriageService>();
        triage.EvaluateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TriageVerdict(true, "In scope."));
        var activity = Build(github, triage, enabled: true);

        var result = await activity.RunAsync(new FakeTriageActivityContext(), MakeInput());

        result.IsRelevant.Should().BeTrue();
        await github.DidNotReceive().MoveItemAsync(Arg.Any<string>(), Arg.Any<ProjectState>(), Arg.Any<ProjectState>(), Arg.Any<CancellationToken>());
        await github.DidNotReceive().AddItemCommentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task When_enabled_and_not_relevant_comments_and_moves_item_to_Backlog()
    {
        var github = Substitute.For<IGitHubProjectService>();
        var triage = Substitute.For<ITriageService>();
        triage.EvaluateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TriageVerdict(false, "Targets a different repository."));
        var activity = Build(github, triage, enabled: true);

        var input = MakeInput("PVTI_reject");
        var result = await activity.RunAsync(new FakeTriageActivityContext(), input);

        result.IsRelevant.Should().BeFalse();

        await github.Received(1).MoveItemAsync(
            "PVTI_reject", ProjectState.Ready, ProjectState.Backlog, Arg.Any<CancellationToken>());

        await github.Received(1).AddItemCommentAsync(
            "I_node_PVTI_reject",
            Arg.Is<string>(c => c.Contains("Targets a different repository.")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rejection_comment_is_posted_before_the_item_is_moved()
    {
        // Ordering guard: a maintainer must see the explanation comment on the item; posting it
        // after the move is still fine functionally, but we comment first so the reason is attached
        // even if the move call fails.
        var github = Substitute.For<IGitHubProjectService>();
        var triage = Substitute.For<ITriageService>();
        triage.EvaluateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TriageVerdict(false, "Out of scope."));
        var activity = Build(github, triage, enabled: true);

        await activity.RunAsync(new FakeTriageActivityContext(), MakeInput());

        Received.InOrder(() =>
        {
            github.AddItemCommentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            github.MoveItemAsync(Arg.Any<string>(), ProjectState.Ready, ProjectState.Backlog, Arg.Any<CancellationToken>());
        });
    }
}

/// <summary>Minimal concrete <see cref="WorkflowActivityContext"/> for activity unit tests.</summary>
file sealed class FakeTriageActivityContext : WorkflowActivityContext
{
    public override Dapr.Workflow.Abstractions.TaskIdentifier Identifier => "fake-task-name";
    public override string InstanceId => "fake-instance-id";
    public override string TaskExecutionKey => "fake-task-key";
}
