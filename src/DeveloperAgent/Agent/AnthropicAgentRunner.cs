using System.Text.Json.Nodes;
using Anthropic.SDK.Common;
using Anthropic.SDK.Messaging;
using DeveloperAgent.Agent.Tools;
using DeveloperAgent.Configuration;
using DeveloperAgent.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Agent;

/// <summary>
/// Drives the Anthropic model conversation loop for a single task.
/// Registered as a singleton; each call to <see cref="RunAsync"/> is stateless (session is local).
/// </summary>
public sealed class AnthropicAgentRunner : IAgentRunner
{
    private const int MaxTokens = 32_000;

    private readonly IAnthropicClient _client;
    private readonly PersonaLoader _personaLoader;
    private readonly AgentOptions _options;
    private readonly IReadOnlyList<ITool> _tools;
    private readonly ILogger<AnthropicAgentRunner> _logger;

    // Effort → extended-thinking budget (confirmed present in Anthropic.SDK 5.10.0)
    private static int? MapEffort(string effort) => effort.ToLowerInvariant() switch
    {
        "xhigh"  => 8000,
        "high"   => 4000,
        "medium" => 2000,
        "low"    => 1000,
        _        => null
    };

    // Constructor is internal because IAnthropicClient is internal.
    // DI resolves this at runtime; InternalsVisibleTo allows tests to construct it too.
    internal AnthropicAgentRunner(
        IAnthropicClient client,
        PersonaLoader personaLoader,
        IOptions<AgentOptions> options,
        IEnumerable<ITool> tools,
        ILogger<AnthropicAgentRunner> logger)
    {
        _client = client;
        _personaLoader = personaLoader;
        _options = options.Value;
        _tools = [..tools];
        _logger = logger;
    }

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct)
    {
        var session = new AgentSession();
        var context = new ToolContext(session, request.Workspace, request.Item);

        // Build the kickoff user message
        var kickoff = BuildKickoffMessage(request);
        session.History.Add(new Message(RoleType.User, kickoff));

        // Build the SDK tools list once
        var sdkTools = BuildSdkTools();

        // Effort → thinking budget
        int? thinkingBudget = MapEffort(_options.Effort);

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                AnthropicResponse response;
                try
                {
                    response = await _client.SendAsync(
                        new AnthropicRequest(
                            SystemPrompt: _personaLoader.Persona,
                            Messages: session.History,
                            Tools: sdkTools,
                            Model: _options.Model,
                            MaxTokens: MaxTokens,
                            Temperature: 0m,
                            ThinkingBudgetTokens: thinkingBudget),
                        ct);
                }
                catch (AnthropicApiException ex)
                {
                    _logger.LogError(ex, "Anthropic API error after retries");
                    return new AgentRunResult(
                        AgentRunOutcome.ApiError,
                        session.CreatedPullRequest,
                        session.TurnsUsed,
                        session.ToolCallsUsed,
                        $"API error: {ex.Message}");
                }

                // Append assistant message to history
                session.History.Add(response.AssistantMessage);

                // Count this turn
                session.TurnsUsed++;

                // Find tool_use blocks
                var toolUseBlocks = response.ContentBlocks
                    .OfType<ToolUseContent>()
                    .ToList();

                if (toolUseBlocks.Count == 0)
                {
                    // Text-only response → task completed
                    var textContent = response.ContentBlocks
                        .OfType<TextContent>()
                        .FirstOrDefault();
                    session.FinalAssistantText = textContent?.Text;

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

                // Check hard cap BEFORE processing tools (so we terminate early)
                if (session.TurnsUsed > _options.MaxModelTurnsHardCap)
                {
                    _logger.LogWarning("Hard cap reached: {Turns} turns", session.TurnsUsed);
                    return new AgentRunResult(
                        AgentRunOutcome.HardCapReached,
                        session.CreatedPullRequest,
                        session.TurnsUsed,
                        session.ToolCallsUsed,
                        $"Hard cap of {_options.MaxModelTurnsHardCap} model turns reached.");
                }

                // Process tool calls and collect results
                var toolResultContents = new List<ContentBase>();
                foreach (var toolUse in toolUseBlocks)
                {
                    var tool = _tools.FirstOrDefault(t => t.Name == toolUse.Name);
                    if (tool is null)
                    {
                        toolResultContents.Add(new ToolResultContent
                        {
                            ToolUseId = toolUse.Id,
                            Content = [new TextContent { Text = $"Unknown tool: {toolUse.Name}" }],
                            IsError = true
                        });
                        session.ToolCallsUsed++;
                        continue;
                    }

                    _logger.LogDebug("Invoking tool {ToolName}", tool.Name);

                    ToolResult toolResult;
                    try
                    {
                        toolResult = await tool.InvokeAsync(toolUse.Input, context, ct);
                    }
                    catch (SandboxViolationException ex)
                    {
                        _logger.LogWarning(ex, "Sandbox violation from tool {ToolName}", tool.Name);
                        return new AgentRunResult(
                            AgentRunOutcome.SandboxViolation,
                            session.CreatedPullRequest,
                            session.TurnsUsed,
                            session.ToolCallsUsed,
                            $"Sandbox violation in tool '{tool.Name}': {SanitizeMessage(ex.Message)}");
                    }

                    session.ToolCallsUsed++;
                    toolResultContents.Add(new ToolResultContent
                    {
                        ToolUseId = toolUse.Id,
                        Content = [new TextContent { Text = toolResult.Content }],
                        IsError = toolResult.IsError
                    });
                }

                // Append all tool results as a single user message
                session.History.Add(new Message
                {
                    Role = RoleType.User,
                    Content = toolResultContents
                });
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Agent run cancelled after {Turns} turns", session.TurnsUsed);
            return new AgentRunResult(
                AgentRunOutcome.Cancelled,
                session.CreatedPullRequest,
                session.TurnsUsed,
                session.ToolCallsUsed,
                "Run cancelled by caller.");
        }
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

    private IList<Anthropic.SDK.Common.Tool> BuildSdkTools()
    {
        var list = new List<Anthropic.SDK.Common.Tool>();
        foreach (var tool in _tools)
        {
            list.Add(new Anthropic.SDK.Common.Function(tool.Name, tool.Description, tool.InputSchema));
        }
        return list;
    }

    private static string SanitizeMessage(string message)
    {
        // Avoid leaking sensitive path info in the termination reason
        if (message.Length > 200)
            return message[..200] + "...";
        return message;
    }
}
