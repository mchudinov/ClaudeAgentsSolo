using DeveloperAgent.Dashboard;
using Serilog.Events;
using Serilog.Parsing;

namespace DeveloperAgent.Tests.Dashboard;

public class RecentLogBufferTests
{
    private static LogEvent Event(string message, LogEventLevel level = LogEventLevel.Information)
    {
        var template = new MessageTemplateParser().Parse(message);
        return new LogEvent(
            DateTimeOffset.UtcNow,
            level,
            exception: null,
            template,
            properties: []);
    }

    [Fact]
    public void Emit_then_Recent_returns_rendered_entries_in_chronological_order()
    {
        var buffer = new RecentLogBuffer(capacity: 8);

        buffer.Emit(Event("first"));
        buffer.Emit(Event("second"));

        var entries = buffer.Recent().ToList();

        entries.Should().HaveCount(2);
        entries[0].Message.Should().Be("first");
        entries[1].Message.Should().Be("second");
    }

    [Fact]
    public void Recent_caps_at_capacity_keeping_the_newest_entries()
    {
        var buffer = new RecentLogBuffer(capacity: 3);

        for (var i = 1; i <= 5; i++)
        {
            buffer.Emit(Event($"msg-{i}"));
        }

        var entries = buffer.Recent().ToList();

        entries.Should().HaveCount(3);
        entries.Select(e => e.Message).Should().ContainInOrder("msg-3", "msg-4", "msg-5");
    }

    [Fact]
    public void Recent_take_returns_only_the_newest_n()
    {
        var buffer = new RecentLogBuffer(capacity: 10);
        for (var i = 1; i <= 5; i++)
        {
            buffer.Emit(Event($"msg-{i}"));
        }

        var entries = buffer.Recent(take: 2).ToList();

        entries.Should().HaveCount(2);
        entries.Select(e => e.Message).Should().ContainInOrder("msg-4", "msg-5");
    }

    [Fact]
    public void Entry_carries_level_and_timestamp()
    {
        var buffer = new RecentLogBuffer(capacity: 4);
        buffer.Emit(Event("boom", LogEventLevel.Error));

        var entry = buffer.Recent().Single();

        entry.Level.Should().Be(LogEventLevel.Error);
        entry.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Constructor_rejects_non_positive_capacity()
    {
        var act = () => new RecentLogBuffer(capacity: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
