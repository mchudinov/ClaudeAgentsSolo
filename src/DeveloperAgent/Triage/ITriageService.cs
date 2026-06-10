namespace DeveloperAgent.Triage;

/// <summary>
/// The verdict of a single relevance-triage evaluation.
/// </summary>
/// <param name="IsRelevant">
/// <see langword="true"/> when the item is in-scope for the project AND within the agent's skill (or
/// when the decision could not be made confidently — triage fails open so it never silently parks
/// work it is unsure about; the Backlog column is write-only and a wrong rejection is unrecoverable).
/// </param>
/// <param name="Reason">A concise human-readable justification, surfaced in the rejection comment and logs.</param>
public sealed record TriageVerdict(bool IsRelevant, string Reason);

/// <summary>
/// Judges whether a GitHub work item is worth the agent's effort before any expensive work begins.
/// Host policy: the relevance criteria (project scope + agent skill) are specific to this agent, so
/// this lives in <c>DeveloperAgent</c> rather than an agent-neutral <c>AgenticTools</c> library.
/// </summary>
public interface ITriageService
{
    /// <summary>
    /// Evaluates the item described by <paramref name="title"/> and <paramref name="bodyMarkdown"/>.
    /// Never throws for model/transport failures — those resolve to a relevant (fail-open) verdict.
    /// </summary>
    Task<TriageVerdict> EvaluateAsync(string title, string bodyMarkdown, CancellationToken ct);
}
