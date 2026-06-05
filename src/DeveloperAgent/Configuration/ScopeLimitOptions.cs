namespace DeveloperAgent.Configuration;

/// <summary>
/// Config-driven scope-limit policy for a single task (LLD §P2-H). Bound from the
/// <c>ScopeLimits</c> configuration section. Every limit is a hard cap; on breach
/// the agent halts and surfaces the breach (comment + halt) rather than proceeding.
/// <para>
/// The pre-push diff-scope caps (<c>MaxChangedFiles</c>/<c>MaxChangedLines</c>) were carved
/// out into <c>DiffScopeLimitOptions</c> in Step-50 (which moved to the <c>Agent.Workspace</c>
/// library with the git client in Step-51); they still bind from this same <c>ScopeLimits</c>
/// section. This record retains the run/PR-policy limits.
/// </para>
/// </summary>
public sealed record ScopeLimitOptions
{
    /// <summary>
    /// Per-agent-run wall-clock budget (seconds). Enforced in
    /// <see cref="DeveloperAgent.Agent.AnthropicAgentRunner"/> via a linked
    /// timeout <see cref="System.Threading.CancellationTokenSource"/>; on breach the
    /// run ends with <c>AgentRunOutcome.TimeLimitExceeded</c>.
    /// </summary>
    public int MaxExecutionTimeSeconds { get; init; } = 1_800;

    /// <summary>
    /// Hard cap on consecutive model turns before the agent run is halted.
    /// Replaces the phase-1 single-turn-cap constant on <c>AgentOptions</c>.
    /// </summary>
    public int MaxModelTurns { get; init; } = 40;

    /// <summary>
    /// Hard cap on local tool invocations (the in-process <see cref="DeveloperAgent.Agent.Tools.ITool"/>
    /// adapters) within a single agent run. Enforced against
    /// <c>AgentRunState.ToolCallsUsed</c>; on breach the run ends with
    /// <c>AgentRunOutcome.ToolCallLimitReached</c>.
    /// </summary>
    public int MaxToolCalls { get; init; } = 200;

    /// <summary>
    /// Maximum attempts (including the first) the Dapr Workflow engine will make per
    /// activity. This is the single source feeding <c>TaskInput.MaxRetryAttempts</c>
    /// (the activity-level transient-retry cap). The actor's durable
    /// <c>ProgrammingTaskState.RetryCount</c> is the persisted tracking surface.
    /// </summary>
    public int MaxRetryCount { get; init; } = 3;

    /// <summary>
    /// Composite PR-size limit: a PR is blocked from opening when its changed-file
    /// count exceeds <see cref="MaxPRChangedFiles"/> OR its changed-line count
    /// exceeds <see cref="MaxPRChangedLines"/>. Enforced in
    /// <see cref="DeveloperAgent.Agent.Tools.CreatePullRequestTool"/> with an
    /// explanatory comment posted to the item.
    /// </summary>
    public int MaxPRChangedFiles { get; init; } = 50;

    /// <summary>Composite PR-size limit: max changed lines (added + deleted) for an opened PR.</summary>
    public int MaxPRChangedLines { get; init; } = 2_000;
}
