namespace DeveloperAgent.Configuration;

/// <summary>
/// Column/status labels used to move items through the project board, bound from <c>GitHub:States</c>.
/// This is developer-agent policy — it binds the agent's <c>ProjectState</c> lifecycle to the actual
/// board column names — so it lives in the host, not in the agent-neutral GitHub library.
/// </summary>
public sealed record ProjectStateNames
{
    /// <summary>Holding column for parked items — rejected by triage or after a failed attempt.
    /// The agent moves items here but never picks them up again.</summary>
    public string Backlog { get; init; } = "Backlog";

    /// <summary>Items that are ready for the agent to pick up.</summary>
    public string Ready { get; init; } = "Ready";

    /// <summary>Items currently being implemented by the agent.</summary>
    public string InProgress { get; init; } = "In Progress";

    /// <summary>Items whose PR is open and awaiting review.</summary>
    public string InReview { get; init; } = "In Review";

    /// <summary>Items whose PR has been merged.</summary>
    public string Done { get; init; } = "Done";
}
