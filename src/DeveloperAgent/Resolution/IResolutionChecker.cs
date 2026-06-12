namespace DeveloperAgent.Resolution;

/// <summary>
/// The verdict of a single "is this item already implemented in the real code?" evaluation.
/// </summary>
/// <param name="IsAlreadyResolved">
/// <see langword="true"/> ONLY when the work the item describes is <em>clearly and verifiably</em>
/// already present in the working tree. The check is deliberately asymmetric and fail-open: any
/// uncertainty, partial match, or error resolves to <see langword="false"/> (proceed to implement).
/// A false positive abandons real work to the write-only Backlog column and is unrecoverable, whereas
/// a false negative merely re-implements something that existed — so the bar for <c>true</c> is high.
/// </param>
/// <param name="Reason">A concise human-readable justification, surfaced in the park comment and logs.</param>
public sealed record ResolutionVerdict(bool IsAlreadyResolved, string Reason);

/// <summary>
/// Judges whether the work described by a GitHub item is already implemented in the checked-out
/// working tree, before the agent spends a full implementation round on it. Host policy (it inspects
/// the real repository contents and encodes the conservative fail-open bias), so it lives in
/// <c>DeveloperAgent</c> rather than an agent-neutral <c>AgenticTools</c> library.
/// </summary>
public interface IResolutionChecker
{
    /// <summary>
    /// Evaluates whether the item (<paramref name="title"/>, <paramref name="bodyMarkdown"/>, and its
    /// existing <paramref name="comments"/>) is already implemented in the working tree rooted at
    /// <paramref name="workspacePath"/>. Never throws for model/transport/IO failures — those resolve
    /// to a not-resolved (fail-open) verdict so a flaky call can never abandon legitimate work.
    /// </summary>
    Task<ResolutionVerdict> EvaluateAsync(
        string title, string bodyMarkdown, string comments, string workspacePath, CancellationToken ct);
}
