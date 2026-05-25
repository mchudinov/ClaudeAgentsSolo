using System.Text.Json;
using System.Text.Json.Nodes;
using DeveloperAgent.Agent.Tools;
using DeveloperAgent.Workspace;
using Microsoft.Extensions.AI;

namespace DeveloperAgent.Agent;

/// <summary>
/// Adapts an <see cref="ITool"/> to Microsoft Agent Framework's <see cref="AIFunction"/> contract.
/// <para>
/// One instance is created per agent run so it can close over the run's mutable
/// <see cref="ToolContext"/> (the session captures tool-call counts and the created PR).
/// </para>
/// <para>
/// On <see cref="SandboxViolationException"/>, the exception is stashed on
/// <see cref="AgentSession.SandboxViolation"/> and re-thrown — Microsoft Agent Framework's
/// <c>FunctionInvokingChatClient</c> with <c>MaximumConsecutiveErrorsPerRequest = 0</c>
/// terminates the loop and surfaces the exception (wrapped in <see cref="AggregateException"/>)
/// to the runner, which then unwraps and returns the <c>SandboxViolation</c> outcome.
/// </para>
/// </summary>
internal sealed class MafToolAdapter : AIFunction
{
    private readonly ITool _tool;
    private readonly ToolContext _context;
    private readonly JsonElement _schemaElement;

    public MafToolAdapter(ITool tool, ToolContext context)
    {
        _tool = tool;
        _context = context;
        // Convert the tool's JsonNode schema into a JsonElement so it can be served
        // via AIFunctionDeclaration.JsonSchema to Microsoft Agent Framework.
        _schemaElement = JsonSerializer.SerializeToElement(tool.InputSchema);
    }

    /// <inheritdoc/>
    public override string Name => _tool.Name;

    /// <inheritdoc/>
    public override string Description => _tool.Description;

    /// <inheritdoc/>
    public override JsonElement JsonSchema => _schemaElement;

    /// <inheritdoc/>
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        // The function call comes in as named arguments; rebuild the JsonNode the tool expects.
        var inputObject = new JsonObject();
        foreach (var kvp in arguments)
        {
            inputObject[kvp.Key] = kvp.Value switch
            {
                null => null,
                JsonElement je => JsonNode.Parse(je.GetRawText()),
                JsonNode jn => jn.DeepClone(),
                string s => JsonValue.Create(s),
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                double d => JsonValue.Create(d),
                _ => JsonNode.Parse(JsonSerializer.Serialize(kvp.Value)),
            };
        }

        ToolResult result;
        try
        {
            result = await _tool.InvokeAsync(inputObject, _context, cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxViolationException ex)
        {
            // Latch the violation on the session so the runner can surface it as
            // AgentRunOutcome.SandboxViolation even after MAF wraps the exception.
            _context.Session.SandboxViolation = (_tool.Name, ex);
            throw;
        }

        _context.Session.ToolCallsUsed++;

        return new { is_error = result.IsError, content = result.Content };
    }
}
