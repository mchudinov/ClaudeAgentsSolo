using System.Text.Json;

namespace Agent.Memory.Tests;

/// <summary>
/// Verifies the persisted <see cref="AgentSession"/> record round-trips through
/// <see cref="JsonSerializer"/> intact — proves Dapr state (which uses System.Text.Json
/// by default) can serialize/deserialize the entity without losing fields.
/// </summary>
public sealed class AgentSessionSerializationTests
{
    [Fact]
    public void Round_trips_all_fields_through_System_Text_Json()
    {
        var original = new AgentSession(
            AgentId: "host-42",
            ProjectItemId: "PVTI_abc123",
            CurrentStep: "AcquireTaskActivity",
            LastActivityTimestampUtc: new DateTimeOffset(2026, 5, 28, 12, 34, 56, TimeSpan.Zero),
            Summary: "trace line");

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<AgentSession>(json);

        roundTripped.Should().NotBeNull();
        roundTripped!.AgentId.Should().Be("host-42");
        roundTripped.ProjectItemId.Should().Be("PVTI_abc123");
        roundTripped.CurrentStep.Should().Be("AcquireTaskActivity");
        roundTripped.LastActivityTimestampUtc.Should().Be(original.LastActivityTimestampUtc);
        roundTripped.Summary.Should().Be("trace line");
    }

    [Fact]
    public void Round_trips_with_null_summary()
    {
        var original = new AgentSession(
            AgentId: "host-7",
            ProjectItemId: "PVTI_xyz",
            CurrentStep: "CreateBranchActivity",
            LastActivityTimestampUtc: DateTimeOffset.UtcNow,
            Summary: null);

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<AgentSession>(json);

        roundTripped.Should().NotBeNull();
        roundTripped!.Summary.Should().BeNull();
    }
}
