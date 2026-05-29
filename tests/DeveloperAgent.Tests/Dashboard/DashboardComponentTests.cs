using Bunit;
using DeveloperAgent.Components.Dashboard;
using DeveloperAgent.Dashboard;
using DeveloperAgent.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Serilog.Events;
using Serilog.Parsing;

namespace DeveloperAgent.Tests.Dashboard;

public class DashboardComponentTests : BunitContext, IAsyncLifetime
{
    public DashboardComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
    }

    // MudBlazor registers IAsyncDisposable-only services (e.g. KeyInterceptorService).
    // bUnit disposes its service provider asynchronously in DisposeAsync; routing teardown
    // through IAsyncLifetime ensures that path runs instead of the sync Dispose that would
    // throw on an async-only service.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private static TaskState RunningTask() => new(
        ProjectItemId: "PVTI_1",
        IssueNumber: 27,
        Title: "Operator dashboard",
        Phase: TaskPhase.AgentRunning,
        BranchName: "Step-27-operator-dashboard",
        PullRequestNumber: 42,
        LastError: null,
        StartedAtUtc: DateTimeOffset.UtcNow,
        UpdatedAtUtc: DateTimeOffset.UtcNow,
        PullRequestOpenedAtUtc: null,
        LastReviewPolledAtUtc: null);

    // ── TaskStatusPanel rendering ────────────────────────────────────────────

    [Fact]
    public void TaskStatusPanel_renders_title_issue_phase_and_branch_of_the_running_task()
    {
        Services.AddSingleton<ITaskStateStore>(new FakeTaskStateStore(RunningTask()));

        var cut = Render<TaskStatusPanel>();

        cut.Find("[data-testid=task-title]").TextContent.Should().Contain("Operator dashboard");
        cut.Find("[data-testid=task-issue]").TextContent.Should().Contain("#27");
        cut.Find("[data-testid=task-phase]").TextContent.Should().Contain("In progress");
        cut.Find("[data-testid=task-branch]").TextContent.Should().Contain("Step-27-operator-dashboard");
    }

    [Fact]
    public void TaskStatusPanel_renders_pull_request_link_when_repository_url_is_supplied()
    {
        Services.AddSingleton<ITaskStateStore>(new FakeTaskStateStore(RunningTask()));

        var cut = Render<TaskStatusPanel>(p => p.Add(c => c.RepositoryUrl, "https://github.com/owner/repo"));

        var link = cut.Find("[data-testid=task-pr-link]");
        link.TextContent.Should().Contain("#42");
        link.GetAttribute("href").Should().Be("https://github.com/owner/repo/pull/42");
    }

    [Fact]
    public void TaskStatusPanel_shows_idle_message_when_no_task_is_active()
    {
        Services.AddSingleton<ITaskStateStore>(new FakeTaskStateStore(current: null));

        var cut = Render<TaskStatusPanel>();

        cut.FindAll("[data-testid=task-idle]").Should().HaveCount(1);
        cut.FindAll("[data-testid=task-title]").Should().BeEmpty();
    }

    // ── RecentLogPanel rendering ─────────────────────────────────────────────

    [Fact]
    public void RecentLogPanel_renders_one_entry_per_buffered_log_event()
    {
        var buffer = new RecentLogBuffer(capacity: 10);
        buffer.Emit(LogEvent("agent started"));
        buffer.Emit(LogEvent("branch created"));
        Services.AddSingleton<IRecentLogBuffer>(buffer);

        var cut = Render<RecentLogPanel>();

        var entries = cut.FindAll("[data-testid=log-entry]");
        entries.Should().HaveCount(2);
        cut.Markup.Should().Contain("agent started");
        cut.Markup.Should().Contain("branch created");
    }

    [Fact]
    public void RecentLogPanel_shows_empty_message_when_buffer_is_empty()
    {
        Services.AddSingleton<IRecentLogBuffer>(new RecentLogBuffer(capacity: 10));

        var cut = Render<RecentLogPanel>();

        cut.FindAll("[data-testid=log-empty]").Should().HaveCount(1);
        cut.FindAll("[data-testid=log-entry]").Should().BeEmpty();
    }

    // ── OperatorActions dispatch ─────────────────────────────────────────────

    [Fact]
    public void OperatorActions_dispatches_pause_to_the_command_service_on_click()
    {
        var spy = new SpyOperatorCommandService();
        Services.AddSingleton<IOperatorCommandService>(spy);
        Services.AddSingleton<ITaskStateStore>(new FakeTaskStateStore(RunningTask()));

        var cut = Render<OperatorActions>();
        cut.Find("[data-testid=action-pause]").Click();

        spy.Received.Should().ContainSingle().Which.Should().Be(OperatorCommand.Pause);
    }

    [Fact]
    public void OperatorActions_dispatches_resume_and_cancel_to_the_command_service()
    {
        var spy = new SpyOperatorCommandService();
        Services.AddSingleton<IOperatorCommandService>(spy);
        Services.AddSingleton<ITaskStateStore>(new FakeTaskStateStore(RunningTask()));

        var cut = Render<OperatorActions>();
        cut.Find("[data-testid=action-resume]").Click();
        cut.Find("[data-testid=action-cancel]").Click();

        spy.Received.Should().ContainInOrder(OperatorCommand.Resume, OperatorCommand.Cancel);
    }

    [Fact]
    public void OperatorActions_renders_the_dispatch_result()
    {
        var spy = new SpyOperatorCommandService(projectItemId: "PVTI_1");
        Services.AddSingleton<IOperatorCommandService>(spy);
        Services.AddSingleton<ITaskStateStore>(new FakeTaskStateStore(RunningTask()));

        var cut = Render<OperatorActions>();
        cut.Find("[data-testid=action-pause]").Click();

        cut.Find("[data-testid=action-result]").TextContent.Should().Contain("Pause requested for PVTI_1");
    }

    [Fact]
    public void OperatorActions_disables_buttons_when_no_task_is_active()
    {
        Services.AddSingleton<IOperatorCommandService>(new SpyOperatorCommandService(projectItemId: null));
        Services.AddSingleton<ITaskStateStore>(new FakeTaskStateStore(current: null));

        var cut = Render<OperatorActions>();

        cut.Find("[data-testid=action-pause]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=action-cancel]").HasAttribute("disabled").Should().BeTrue();
    }

    private static LogEvent LogEvent(string message)
    {
        var template = new MessageTemplateParser().Parse(message);
        return new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, template, []);
    }
}
