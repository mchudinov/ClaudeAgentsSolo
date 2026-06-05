using System.Runtime.Serialization;
using DeveloperAgent.Actors;
using DeveloperAgent.Lifecycle;
using FluentAssertions;

namespace DeveloperAgent.Tests.Actors;

/// <summary>
/// Guards the wire contract of <see cref="ProgrammingTaskState"/>.
/// </summary>
/// <remarks>
/// <para>
/// The lifecycle store talks to <see cref="ProgrammingTaskActor"/> through a Dapr
/// <c>CreateActorProxy&lt;IProgrammingTaskActor&gt;</c> (the strongly-typed
/// <i>remoting</i> path). Dapr's remoting transport serializes actor method
/// arguments and return values with <see cref="DataContractSerializer"/> — unlike
/// the actor's own state store, which uses JSON and therefore tolerates plain
/// records.
/// </para>
/// <para>
/// <see cref="IProgrammingTaskActor.GetStateAsync"/> returns
/// <see cref="ProgrammingTaskState"/>, so the type must be DataContract-serializable.
/// When it was a plain positional record (no <c>[DataContract]</c>/<c>[DataMember]</c>
/// and no parameterless constructor) the proxy call threw
/// <c>InvalidDataContractException: Type 'DeveloperAgent.Actors.ProgrammingTaskState'
/// cannot be serialized</c>, which surfaced at startup as
/// <c>DaprActorTaskStateStore.TryGetPersistedStateAsync</c> failing and in-flight
/// item recovery being silently skipped. These tests reproduce that failure at the
/// serializer level and lock in the round-trip.
/// </para>
/// </remarks>
public sealed class ProgrammingTaskStateSerializationTests
{
    private static ProgrammingTaskState RoundTrip(ProgrammingTaskState original)
    {
        var serializer = new DataContractSerializer(typeof(ProgrammingTaskState));

        using var stream = new MemoryStream();
        serializer.WriteObject(stream, original);
        stream.Position = 0;
        return (ProgrammingTaskState)serializer.ReadObject(stream)!;
    }

    [Fact]
    public void Fully_populated_state_round_trips_through_DataContractSerializer()
    {
        var original = new ProgrammingTaskState(
            ProjectItemId: "PVTI_lAHOACe5l84BVhWCzguUSFw",
            AgentId: "agent-A",
            Phase: TaskPhase.AwaitingReview,
            BranchName: "agent/step-29-foo",
            PullRequestNumber: 123,
            RetryCount: 2,
            ApprovalStatus: ApprovalStatus.WaitingForReview);

        var roundTripped = RoundTrip(original);

        roundTripped.Should().Be(original);
    }

    [Fact]
    public void Empty_state_with_null_members_round_trips_through_DataContractSerializer()
    {
        // Recovery reads the actor's freshly-activated Empty() snapshot for items
        // that never advanced — every nullable member is null here, so this guards
        // the most common recovery payload.
        var original = ProgrammingTaskState.Empty("PVTI_only-claimed");

        var roundTripped = RoundTrip(original);

        roundTripped.Should().Be(original);
        roundTripped.AgentId.Should().BeNull();
        roundTripped.BranchName.Should().BeNull();
        roundTripped.PullRequestNumber.Should().BeNull();
    }
}
