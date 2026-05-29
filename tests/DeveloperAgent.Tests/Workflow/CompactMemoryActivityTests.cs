using Dapr.Workflow;
using DeveloperAgent.AgentMemory;
using DeveloperAgent.Workflow.Activities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DeveloperAgent.Tests.Workflow;

/// <summary>
/// Unit tests for <see cref="CompactMemoryActivity"/> (Step-25, P2-J). The activity composes a
/// task-completion summary covering the five required sections (completed task, changed files,
/// decisions, test results, unresolved risks) and persists it under the
/// <c>task-memory:{projectItemId}</c> namespace via <see cref="IAgentMemoryStore"/> so the
/// Step-20 <see cref="DaprAgentMemoryContextProvider"/> reads it back on a future run.
/// </summary>
public sealed class CompactMemoryActivityTests
{
    private const string ProjectItemId = "PVTI_abc";

    private static readonly DateTimeOffset FixedNow =
        new(2026, 5, 30, 12, 0, 0, TimeSpan.Zero);

    private static CompactMemoryActivity BuildActivity(
        IAgentMemoryStore store, FakeTimeProvider? clock = null) =>
        new(NullLogger<CompactMemoryActivity>.Instance, store,
            clock ?? new FakeTimeProvider(FixedNow));

    private static CompactMemoryActivityInput SampleInput() => new(
        ProjectItemId: ProjectItemId,
        Title: "Add feature X",
        ContentNumber: 42,
        BranchName: "agent/feature-x",
        PullRequestNumber: 7,
        ToolCallsUsed: 13,
        Decisions: "Reused ISummarizer rather than adding a new seam.",
        ChangedFiles: new[] { "src/Foo.cs", "tests/FooTests.cs" },
        TestResults: "12 passed, 0 failed",
        UnresolvedRisks: "Integration path not exercised on Windows.");

    private static WorkflowActivityContext MakeContext() => new FakeDoneActivityContext();

    [Fact]
    public async Task Persists_summary_under_task_memory_namespace()
    {
        var store = new InMemoryAgentMemoryStore();
        var activity = BuildActivity(store);

        await activity.RunAsync(MakeContext(), SampleInput());

        var stored = await store.LoadTaskMemoriesAsync(ProjectItemId, CancellationToken.None);
        stored.Should().NotBeNull();
        stored!.Should().ContainSingle(
            because: "the compacted task-completion summary is stored as a single memory entry");
    }

    [Fact]
    public async Task Summary_covers_all_five_required_sections()
    {
        var store = new InMemoryAgentMemoryStore();
        var activity = BuildActivity(store);

        await activity.RunAsync(MakeContext(), SampleInput());

        var summary = (await store.LoadTaskMemoriesAsync(ProjectItemId, CancellationToken.None))!.Single();

        // Acceptance: summary covers completed task, changed files, decisions, test results,
        // unresolved risks. Assert the five section labels are present.
        summary.Should().Contain("Completed task");
        summary.Should().Contain("Changed files");
        summary.Should().Contain("Decisions");
        summary.Should().Contain("Test results");
        summary.Should().Contain("Unresolved risks");
    }

    [Fact]
    public async Task Summary_includes_concrete_task_facts()
    {
        var store = new InMemoryAgentMemoryStore();
        var activity = BuildActivity(store);

        await activity.RunAsync(MakeContext(), SampleInput());

        var summary = (await store.LoadTaskMemoriesAsync(ProjectItemId, CancellationToken.None))!.Single();

        summary.Should().Contain("Add feature X");
        summary.Should().Contain("#42");
        summary.Should().Contain("src/Foo.cs");
        summary.Should().Contain("tests/FooTests.cs");
        summary.Should().Contain("Reused ISummarizer rather than adding a new seam.");
        summary.Should().Contain("12 passed, 0 failed");
        summary.Should().Contain("Integration path not exercised on Windows.");
    }

    [Fact]
    public async Task Empty_optional_fields_render_honest_placeholders()
    {
        var store = new InMemoryAgentMemoryStore();
        var activity = BuildActivity(store);

        var input = new CompactMemoryActivityInput(
            ProjectItemId: ProjectItemId,
            Title: "Doc-only change",
            ContentNumber: 5,
            BranchName: "agent/docs",
            PullRequestNumber: null,
            ToolCallsUsed: 0,
            Decisions: null,
            ChangedFiles: Array.Empty<string>(),
            TestResults: null,
            UnresolvedRisks: null);

        await activity.RunAsync(MakeContext(), input);

        var summary = (await store.LoadTaskMemoriesAsync(ProjectItemId, CancellationToken.None))!.Single();

        // Sparse sections must still appear with an explicit "none recorded" marker so the
        // reader can tell absence apart from a missing section.
        summary.Should().Contain("Changed files");
        summary.Should().Contain("none recorded");
    }

    [Fact]
    public async Task Save_is_idempotent_for_the_same_inputs_and_clock()
    {
        var store = new InMemoryAgentMemoryStore();
        var clock = new FakeTimeProvider(FixedNow);
        var activity = BuildActivity(store, clock);

        await activity.RunAsync(MakeContext(), SampleInput());
        var first = (await store.LoadTaskMemoriesAsync(ProjectItemId, CancellationToken.None))!;

        // Re-run (e.g. workflow replay / retry) with the same inputs and the same clock.
        await activity.RunAsync(MakeContext(), SampleInput());
        var second = (await store.LoadTaskMemoriesAsync(ProjectItemId, CancellationToken.None))!;

        // Idempotent: overwrite semantics keep exactly one entry, byte-identical to the first.
        second.Should().ContainSingle();
        second.Single().Should().Be(first.Single());
    }

    [Fact]
    public async Task Stamps_completion_time_from_the_injected_clock()
    {
        var store = new InMemoryAgentMemoryStore();
        var activity = BuildActivity(store, new FakeTimeProvider(FixedNow));

        await activity.RunAsync(MakeContext(), SampleInput());

        var summary = (await store.LoadTaskMemoriesAsync(ProjectItemId, CancellationToken.None))!.Single();

        // The activity (not the deterministic workflow body) stamps the timestamp.
        summary.Should().Contain("2026-05-30");
    }
}
