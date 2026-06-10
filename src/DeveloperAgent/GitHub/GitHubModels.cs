namespace DeveloperAgent.GitHub;

/// <summary>
/// The developer agent's board lifecycle. This is agent policy — the agent-neutral GitHub library
/// (Agent.GitHub) keys board operations by status-column name and has no opinion about these states;
/// the <see cref="GitHubProjectService"/> facade maps between them.
/// <para>
/// <see cref="Backlog"/> is a holding column the agent moves items INTO (when a task is rejected by
/// triage or an implementation attempt fails) but never picks items OUT of: the facade maps it
/// write-only — <c>ToStatusName</c> knows it, but <c>ToState</c> deliberately does not, so a parked
/// item is never surfaced as workable and the poller cannot re-grab it. Appended last so the
/// existing four members keep their ordinal values.
/// </para>
/// </summary>
public enum ProjectState { Ready, InProgress, InReview, Done, Backlog }

/// <summary>A GitHub Project v2 item that contains an Issue or Pull Request, with its board column
/// resolved to the developer agent's <see cref="ProjectState"/>.</summary>
/// <param name="ProjectItemId">GraphQL node ID of the <c>ProjectV2Item</c>.</param>
/// <param name="ContentNodeId">Issue or PR node ID — used for comments.</param>
/// <param name="ContentNumber">Issue or PR number in the repository.</param>
/// <param name="Title">Issue or PR title.</param>
/// <param name="BodyMarkdown">Issue or PR body in markdown.</param>
/// <param name="State">Current status column on the project board.</param>
public sealed record ProjectItem(
    string ProjectItemId,
    string ContentNodeId,
    int ContentNumber,
    string Title,
    string BodyMarkdown,
    ProjectState State);
