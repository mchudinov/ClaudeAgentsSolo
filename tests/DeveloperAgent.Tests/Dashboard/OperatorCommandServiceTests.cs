using DeveloperAgent.Dashboard;
using DeveloperAgent.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeveloperAgent.Tests.Dashboard;

public class OperatorCommandServiceTests
{
    private static TaskState RunningTask(string id = "PVTI_1") => new(
        ProjectItemId: id,
        IssueNumber: 27,
        Title: "Operator dashboard",
        Phase: TaskPhase.AgentRunning,
        BranchName: "Step-27-operator-dashboard",
        PullRequestNumber: null,
        LastError: null,
        StartedAtUtc: DateTimeOffset.UtcNow,
        UpdatedAtUtc: DateTimeOffset.UtcNow,
        PullRequestOpenedAtUtc: null,
        LastReviewPolledAtUtc: null);

    [Fact]
    public async Task PauseAsync_records_intent_against_the_current_task()
    {
        var store = new InMemoryTaskStateStore();
        store.Set(RunningTask());
        var service = new OperatorCommandService(store, NullLogger<OperatorCommandService>.Instance);

        var result = await service.PauseAsync(CancellationToken.None);

        result.Accepted.Should().BeTrue();
        result.ProjectItemId.Should().Be("PVTI_1");
        result.Command.Should().Be(OperatorCommand.Pause);
        service.LastCommand.Should().NotBeNull();
        service.LastCommand!.Command.Should().Be(OperatorCommand.Pause);
    }

    [Fact]
    public async Task ResumeAsync_records_resume_intent()
    {
        var store = new InMemoryTaskStateStore();
        store.Set(RunningTask());
        var service = new OperatorCommandService(store, NullLogger<OperatorCommandService>.Instance);

        var result = await service.ResumeAsync(CancellationToken.None);

        result.Accepted.Should().BeTrue();
        result.Command.Should().Be(OperatorCommand.Resume);
        service.LastCommand!.Command.Should().Be(OperatorCommand.Resume);
    }

    [Fact]
    public async Task CancelAsync_records_cancel_intent()
    {
        var store = new InMemoryTaskStateStore();
        store.Set(RunningTask());
        var service = new OperatorCommandService(store, NullLogger<OperatorCommandService>.Instance);

        var result = await service.CancelAsync(CancellationToken.None);

        result.Accepted.Should().BeTrue();
        result.Command.Should().Be(OperatorCommand.Cancel);
        service.LastCommand!.Command.Should().Be(OperatorCommand.Cancel);
    }

    [Fact]
    public async Task Commands_are_rejected_when_no_task_is_active()
    {
        var store = new InMemoryTaskStateStore();
        var service = new OperatorCommandService(store, NullLogger<OperatorCommandService>.Instance);

        var result = await service.PauseAsync(CancellationToken.None);

        result.Accepted.Should().BeFalse();
        result.ProjectItemId.Should().BeNull();
        service.LastCommand.Should().BeNull();
    }
}
