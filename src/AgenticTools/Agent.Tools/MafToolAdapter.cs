using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Agent.Tools;

/// <summary>
/// Adapts an <see cref="ITool"/> to Microsoft Agent Framework's <see cref="AIFunction"/> contract.
/// <para>
/// One instance is created per agent run so it can close over the run's <see cref="IToolContext"/>
/// and <see cref="IToolCallBudget"/>.
/// </para>
/// <para>
/// When a tool throws, the optional <c>onToolThrew</c> callback fires with the tool name and the
/// exception <em>before</em> it is re-thrown — the host uses this to latch run-terminating
/// conditions (e.g. a sandbox violation) without this library having to know the host's exception
/// or state types. The re-throw lets Microsoft Agent Framework's <c>FunctionInvokingChatClient</c>
/// (with <c>MaximumConsecutiveErrorsPerRequest = 0</c>) terminate the loop and surface the exception
/// (wrapped in <see cref="AggregateException"/>) to the host's run loop.
/// </para>
/// </summary>
public sealed class MafToolAdapter : AIFunction
{
    private readonly ITool _tool;
    private readonly IToolContext _context;
    private readonly IToolCallBudget _budget;
    private readonly Action<string, Exception>? _onToolThrew;
    private readonly JsonElement _schemaElement;

    /// <summary>Creates an adapter binding <paramref name="tool"/> to a single run's context.</summary>
    /// <param name="tool">The tool to expose to the model.</param>
    /// <param name="context">The per-run context handed to <see cref="ITool.InvokeAsync"/>.</param>
    /// <param name="budget">
    /// The per-run tool-call budget. <see cref="IToolCallBudget.ThrowIfExhausted"/> is consulted
    /// before each invocation and <see cref="IToolCallBudget.Record"/> after each success.
    /// </param>
    /// <param name="onToolThrew">
    /// Optional host hook invoked with <c>(toolName, exception)</c> when the tool throws, before the
    /// exception is re-thrown. Used to latch run-terminating conditions.
    /// </param>
    public MafToolAdapter(
        ITool tool,
        IToolContext context,
        IToolCallBudget budget,
        Action<string, Exception>? onToolThrew = null)
    {
        _tool = tool;
        _context = context;
        _budget = budget;
        _onToolThrew = onToolThrew;
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
        // Scope-limit gate: halt before invoking a tool that would push the run past its budget.
        // This is thrown BEFORE the try below so the host's run loop sees it directly (it is not a
        // tool failure and must not flow through the onToolThrew latch).
        _budget.ThrowIfExhausted();

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
        catch (Exception ex)
        {
            // Let the host latch run-terminating conditions (e.g. a sandbox violation) keyed on the
            // offending tool, then re-throw so MAF terminates the loop. A throwing tool does not
            // consume budget (Record is only reached on success).
            _onToolThrew?.Invoke(_tool.Name, ex);
            throw;
        }

        _budget.Record();

        return new { is_error = result.IsError, content = result.Content };
    }
}
