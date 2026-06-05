using System.Text.Json.Nodes;

namespace Agent.Tools;

/// <summary>A tool the model can call during its execution loop.</summary>
public interface ITool
{
    /// <summary>The model-visible tool name (matches <c>tool_use.name</c> from the API).</summary>
    string Name { get; }

    /// <summary>Description shown to the model in the tool list.</summary>
    string Description { get; }

    /// <summary>JSON Schema for the tool's <c>tool_use.input</c>.</summary>
    JsonNode InputSchema { get; }

    /// <summary>Invokes the tool with the given input.</summary>
    /// <param name="input">The parsed input object as returned by the model.</param>
    /// <param name="context">Context exposing the workspace boundary the tool operates within.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="ToolResult"/> indicating success or error. A tool that detects a sandbox
    /// violation should throw rather than returning an error result, so the host can terminate the
    /// run; the exception type and its handling are a host concern (see the host run loop and the
    /// <c>onToolThrew</c> callback on <see cref="MafToolAdapter"/>).
    /// </returns>
    Task<ToolResult> InvokeAsync(JsonNode input, IToolContext context, CancellationToken ct);
}
