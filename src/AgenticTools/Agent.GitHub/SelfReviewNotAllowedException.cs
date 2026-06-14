namespace Agent.GitHub;

/// <summary>
/// Thrown when a review submission is rejected by GitHub because the authenticated identity is the
/// pull request's own author. GitHub forbids an account from submitting an <c>APPROVE</c> or
/// <c>REQUEST_CHANGES</c> review on its own PR (HTTP 422, e.g. "Can not approve your own pull request").
/// </summary>
/// <remarks>
/// This is the agent-neutral translation of Octokit's <c>ApiValidationException</c> for the self-review
/// case — Octokit types never escape the transport (see <c>OctokitRestTransport.SubmitReviewAsync</c>),
/// so the reviewer host can recognise and report the misconfiguration without referencing Octokit.
/// <para>
/// The actionable cause is almost always that the developer and reviewer agents authenticate as the
/// <em>same</em> GitHub identity. Give the reviewer a distinct identity (a separate bot account or a
/// GitHub App installation token) so its approvals are accepted.
/// </para>
/// <see cref="Exception.Message"/> carries GitHub's own 422 message verbatim so it surfaces in logs.
/// </remarks>
public sealed class SelfReviewNotAllowedException : InvalidOperationException
{
    public SelfReviewNotAllowedException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}
