namespace DeveloperAgent.Configuration;

/// <summary>Agent runtime options — controls model selection, polling cadence, and safety caps.</summary>
public sealed record AgentOptions
{
    /// <summary>Human-readable name logged at startup.</summary>
    public string Name { get; init; } = "DeveloperAgent";

    /// <summary>Anthropic model ID to use for code generation.</summary>
    public string Model { get; init; } = "claude-opus-4-7";

    /// <summary>Reasoning effort level: xhigh | high | medium | low.</summary>
    public string Effort { get; init; } = "xhigh";

    /// <summary>Path to the agent persona markdown file, relative to ContentRootPath.</summary>
    public string PersonaPath { get; init; } = "personas/developer.md";

    /// <summary>How often to poll for new Ready items (seconds).</summary>
    public int PollIntervalSeconds { get; init; } = 60;

    /// <summary>How often to poll for PR review results (seconds).</summary>
    public int ReviewPollIntervalSeconds { get; init; } = 60;

    /// <summary>Hard cap on consecutive model turns before the agent is halted.</summary>
    public int MaxModelTurnsHardCap { get; init; } = 40;

    /// <summary>
    /// Maximum number of attempts (including the first one) that the Dapr Workflow
    /// engine will make for any activity call. Applied via <c>WorkflowRetryPolicy</c>
    /// on every <c>CallActivityAsync</c> in <see cref="DeveloperAgent.Workflow.DeveloperTaskWorkflow"/>.
    /// </summary>
    public int MaxRetryAttempts { get; init; } = 3;

    /// <summary>
    /// Delay (seconds) between the first and second attempt of an activity. Subsequent
    /// retries use exponential back-off (coefficient 2.0, capped at 60 seconds).
    /// </summary>
    public int FirstRetryIntervalSeconds { get; init; } = 2;
}
