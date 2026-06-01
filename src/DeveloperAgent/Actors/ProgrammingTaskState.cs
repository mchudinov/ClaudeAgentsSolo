using System.Runtime.Serialization;
using DeveloperAgent.Lifecycle;

namespace DeveloperAgent.Actors;

/// <summary>
/// Persistent state owned by <see cref="ProgrammingTaskActor"/> — the per-item
/// snapshot kept in the Dapr actor state store.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="TaskState"/> (the lifecycle loop's in-process DTO).
/// The lifecycle loop's <c>ITaskStateStore</c> is swapped to a Dapr-actor-backed
/// implementation in Step-11; that step owns the mapping between the two records.
/// </para>
/// <para>
/// The <c>[DataContract]</c>/<c>[DataMember]</c> annotations are mandatory: this
/// type crosses the Dapr actor <i>remoting</i> proxy boundary as the return value
/// of <see cref="IProgrammingTaskActor.GetStateAsync"/>, and Dapr's remoting
/// transport serializes with <see cref="DataContractSerializer"/>. Without them a
/// positional record has no parameterless constructor, so the serializer throws
/// <c>InvalidDataContractException</c> and in-flight item recovery silently fails.
/// (The actor's own state store uses JSON and does not need these — the remoting
/// proxy does.) See <c>ProgrammingTaskStateSerializationTests</c> for the round-trip guard.
/// </para>
/// </remarks>
[DataContract]
public sealed record ProgrammingTaskState(
    [property: DataMember] string ProjectItemId,
    [property: DataMember] string? AgentId,
    [property: DataMember] TaskPhase Phase,
    [property: DataMember] string? BranchName,
    [property: DataMember] int? PullRequestNumber,
    [property: DataMember] int RetryCount,
    [property: DataMember] ApprovalStatus ApprovalStatus)
{
    /// <summary>
    /// Returns an empty state record for the given project item, used when
    /// the actor is activated and no prior state exists in the store.
    /// </summary>
    public static ProgrammingTaskState Empty(string projectItemId) => new(
        ProjectItemId: projectItemId,
        AgentId: null,
        Phase: TaskPhase.Acquired,
        BranchName: null,
        PullRequestNumber: null,
        RetryCount: 0,
        ApprovalStatus: ApprovalStatus.None);
}

/// <summary>
/// Review approval status tracked by the actor — orthogonal to <see cref="TaskPhase"/>
/// because a PR can be in <see cref="TaskPhase.AwaitingReview"/> and either
/// <see cref="WaitingForReview"/>, <see cref="Approved"/>, or <see cref="ChangesRequested"/>.
/// </summary>
public enum ApprovalStatus
{
    /// <summary>No PR opened yet, or the agent has not yet handed the PR over for review.</summary>
    None,

    /// <summary>PR is open and the agent is polling for a verdict.</summary>
    WaitingForReview,

    /// <summary>Reviewer approved; the workflow may merge / move to Done.</summary>
    Approved,

    /// <summary>Reviewer requested changes; the agent must continue on the same branch.</summary>
    ChangesRequested
}
