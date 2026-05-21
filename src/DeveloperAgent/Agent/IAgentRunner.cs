namespace DeveloperAgent.Agent;

/// <summary>
/// Runs a single task to completion (or a terminal error state) by
/// driving the Anthropic model loop with the registered tools.
/// </summary>
public interface IAgentRunner
{
    /// <summary>
    /// Executes the agent loop for the given request.
    /// Returns when the model produces a text-only response, the hard-cap is hit,
    /// a sandbox violation occurs, the API is unrecoverable, or the token is cancelled.
    /// </summary>
    Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct);
}
