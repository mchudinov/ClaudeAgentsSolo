using DeveloperAgent.Agent.Mcp;
using DeveloperAgent.Agent.Tools;
using DeveloperAgent.Configuration;
using DeveloperAgent.Workspace;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Agent;

/// <summary>
/// Drives the model conversation loop for a single task using Microsoft Agent Framework
/// (<c>Microsoft.Agents.AI</c>) on top of the Anthropic provider
/// (<c>Microsoft.Agents.AI.Anthropic</c>).
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton; each call to <see cref="RunAsync"/> is stateless — a fresh
/// <see cref="AgentSession"/> and a fresh set of <see cref="MafToolAdapter"/> bindings are
/// created per call so tools close over the run's mutable <see cref="ToolContext"/>.
/// </para>
/// <para>
/// The agent is constructed per run because the tool adapters carry per-run context (the
/// session that <c>create_pull_request</c> writes into).
/// </para>
/// </remarks>
public sealed class AnthropicAgentRunner : IAgentRunner
{
    private const int MaxTokens = 32_000;

    private readonly IAgentChatClientFactory _chatClientFactory;
    private readonly PersonaLoader _personaLoader;
    private readonly AgentOptions _options;
    private readonly IReadOnlyList<ITool> _tools;
    private readonly IMcpToolSource? _mcpToolSource;
    private readonly ILogger<AnthropicAgentRunner> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public AnthropicAgentRunner(
        IAgentChatClientFactory chatClientFactory,
        PersonaLoader personaLoader,
        IOptions<AgentOptions> options,
        IEnumerable<ITool> tools,
        ILogger<AnthropicAgentRunner> logger,
        IMcpToolSource? mcpToolSource = null,
        ILoggerFactory? loggerFactory = null)
    {
        _chatClientFactory = chatClientFactory;
        _personaLoader = personaLoader;
        _options = options.Value;
        _tools = [..tools];
        _mcpToolSource = mcpToolSource;
        _logger = logger;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct)
    {
        var session = new AgentSession();
        var context = new ToolContext(session, request.Workspace, request.Item);

        // Wrap every ITool as an AIFunction the agent can invoke. New per-run instances so
        // the adapter closes over THIS session — important for ToolCallsUsed counting and
        // CreatePullRequestTool storing the resulting PullRequest on session.CreatedPullRequest.
        IList<AITool> mafTools = _tools
            .Select(t => (AITool)new MafToolAdapter(t, context))
            .ToList();

        // Append MCP-sourced tools (GitHub MCP, Context7 MCP) when configured. These come
        // through as Microsoft.Extensions.AI.AIFunction-derived McpClientTool instances so
        // no adapter is needed. ToolCallsUsed counts local tools only — see AgentSession.
        if (_mcpToolSource is not null)
        {
            var mcpTools = await _mcpToolSource.GetToolsAsync(ct).ConfigureAwait(false);
            foreach (var mcpTool in mcpTools)
                mafTools.Add(mcpTool);
        }

        // Build the chat client; wrap it with the turn-counting decorator BEFORE handing
        // it to ChatClientAgent (which itself wraps with FunctionInvokingChatClient).
        IChatClient innerChatClient = _chatClientFactory.Create(_options.Model);
        IChatClient countingClient = new TurnCountingChatClient(innerChatClient, session, _options.MaxModelTurnsHardCap);

        var agentOptions = new ChatClientAgentOptions
        {
            Name = "DeveloperAgent",
            ChatOptions = new ChatOptions
            {
                Instructions = _personaLoader.Persona,
                Tools = mafTools,
                MaxOutputTokens = MaxTokens,
                Temperature = 0.0f,
                // Disable parallel tool calls so SandboxViolationException from the FIRST
                // offending tool ends the run without other tools running in parallel.
                AllowMultipleToolCalls = false,
            },
        };

        var agent = new ChatClientAgent(
            countingClient,
            agentOptions,
            _loggerFactory);

        // Configure FunctionInvokingChatClient so a single function exception terminates the
        // loop (instead of being fed back to the model). This is how SandboxViolationException
        // surfaces to the runner. ChatClient property returns the post-decorator chain.
        if (agent.ChatClient.GetService<FunctionInvokingChatClient>() is { } fic)
        {
            fic.MaximumConsecutiveErrorsPerRequest = 0;
            // Keep MAF's own iteration cap aligned with our hard cap as a defence in depth;
            // the authoritative check is in TurnCountingChatClient.
            fic.MaximumIterationsPerRequest = Math.Max(1, _options.MaxModelTurnsHardCap);
            fic.IncludeDetailedErrors = true;
        }

        // Construct the kickoff message. Persona is supplied via ChatOptions.Instructions
        // (the system prompt) so it does NOT go into the user-message list.
        var kickoff = new ChatMessage(ChatRole.User, BuildKickoffMessage(request));

        try
        {
            var agentSession = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
            var response = await agent.RunAsync(kickoff, agentSession, options: null, ct).ConfigureAwait(false);

            session.FinalAssistantText = response.Text;

            _logger.LogInformation(
                "Agent completed: turns={Turns} toolCalls={ToolCalls}",
                session.TurnsUsed, session.ToolCallsUsed);

            return new AgentRunResult(
                AgentRunOutcome.Completed,
                session.CreatedPullRequest,
                session.TurnsUsed,
                session.ToolCallsUsed,
                null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Agent run cancelled after {Turns} turns", session.TurnsUsed);
            return new AgentRunResult(
                AgentRunOutcome.Cancelled,
                session.CreatedPullRequest,
                session.TurnsUsed,
                session.ToolCallsUsed,
                "Run cancelled by caller.");
        }
        catch (Exception ex) when (UnwrapHardCap(ex) is { } hardCap)
        {
            _logger.LogWarning("Hard cap reached: {Turns} turns", session.TurnsUsed);
            return new AgentRunResult(
                AgentRunOutcome.HardCapReached,
                session.CreatedPullRequest,
                session.TurnsUsed,
                session.ToolCallsUsed,
                $"Hard cap of {hardCap.Cap} model turns reached.");
        }
        catch (Exception) when (session.SandboxViolation is not null)
        {
            var (toolName, sandboxEx) = session.SandboxViolation.Value;
            _logger.LogWarning(sandboxEx, "Sandbox violation from tool {ToolName}", toolName);
            return new AgentRunResult(
                AgentRunOutcome.SandboxViolation,
                session.CreatedPullRequest,
                session.TurnsUsed,
                session.ToolCallsUsed,
                $"Sandbox violation in tool '{toolName}': {SanitizeMessage(sandboxEx.Message)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent API error after retries");
            return new AgentRunResult(
                AgentRunOutcome.ApiError,
                session.CreatedPullRequest,
                session.TurnsUsed,
                session.ToolCallsUsed,
                $"API error: {ex.Message}");
        }
    }

    /// <summary>
    /// Walks an exception tree (including <see cref="AggregateException.InnerExceptions"/>)
    /// looking for a <see cref="HardCapReachedException"/>.
    /// </summary>
    private static HardCapReachedException? UnwrapHardCap(Exception ex)
    {
        if (ex is HardCapReachedException hc)
            return hc;
        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.Flatten().InnerExceptions)
            {
                if (inner is HardCapReachedException h)
                    return h;
            }
        }
        if (ex.InnerException is { } inner2)
            return UnwrapHardCap(inner2);
        return null;
    }

    private static string BuildKickoffMessage(AgentRunRequest request)
    {
        var item = request.Item;
        var ws = request.Workspace;
        return
            $"GitHub Project item: #{item.ContentNumber} — {item.Title}\n\n" +
            $"Issue body:\n{item.BodyMarkdown}\n\n" +
            $"Workspace root: {ws.RepoRoot}\n" +
            $"Branch you must use: {ws.BranchName}\n" +
            $"Default branch: {ws.DefaultBranch}\n\n" +
            $"Prior reviewer feedback (if any):\n{request.PriorReviewFeedback ?? "(none — this is the first round)"}";
    }

    private static string SanitizeMessage(string message)
    {
        if (message.Length > 200)
            return message[..200] + "...";
        return message;
    }
}
