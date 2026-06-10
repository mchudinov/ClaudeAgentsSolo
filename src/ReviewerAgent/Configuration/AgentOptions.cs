namespace ReviewerAgent.Configuration;

/// <summary>
/// Agent runtime options — the same <c>Agent</c> settings structure the DeveloperAgent host uses,
/// so both agents are configured identically. Bound from the <c>Agent</c> configuration section.
/// </summary>
/// <remarks>
/// The reviewer is a stateless poller, so it consumes <see cref="Name"/> (startup log),
/// <see cref="Model"/>, <see cref="Effort"/>, <see cref="PersonaPath"/>, and
/// <see cref="PollIntervalSeconds"/>. <see cref="Model"/>/<see cref="PersonaPath"/> are also bound
/// onto the engine's <c>ReviewerOptions</c> and <see cref="PollIntervalSeconds"/> onto
/// <c>ReviewPollingOptions</c> from the same <c>Agent</c> keys (so they cannot drift). Like the
/// DeveloperAgent host, <see cref="Effort"/> is configured but not yet wired into the model call.
/// <see cref="ReviewPollIntervalSeconds"/> and <see cref="FirstRetryIntervalSeconds"/> exist for
/// structural parity with the developer host; the reviewer has no PR-review loop or Dapr-workflow
/// retry, so it does not consume them.
/// </remarks>
public sealed record AgentOptions
{
    /// <summary>Human-readable name logged at startup.</summary>
    public string Name { get; init; } = "ReviewerAgent";

    /// <summary>Anthropic model ID to use for the persona-violation review scan.</summary>
    public string Model { get; init; } = "claude-opus-4-8";

    /// <summary>Reasoning effort level: xhigh | high | medium | low.</summary>
    public string Effort { get; init; } = "xhigh";

    /// <summary>Path to the agent persona markdown file, relative to ContentRootPath.</summary>
    public string PersonaPath { get; init; } = "personas/reviewer.md";

    /// <summary>How often to poll for open PRs to review (seconds).</summary>
    public int PollIntervalSeconds { get; init; } = 60;

    /// <summary>How often to poll for PR review results (seconds). Parity field — unused by the reviewer.</summary>
    public int ReviewPollIntervalSeconds { get; init; } = 60;

    /// <summary>
    /// Delay (seconds) between the first and second retry attempt. Parity field — unused by the
    /// reviewer, which has no Dapr workflow.
    /// </summary>
    public int FirstRetryIntervalSeconds { get; init; } = 2;
}
