using System.Text.Json.Nodes;

namespace DeveloperAgent.Agent.Tools;

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
    /// <param name="context">Context carrying session state, workspace, and item.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="ToolResult"/> indicating success or error.
    /// On <see cref="Workspace.SandboxViolationException"/>, implementations should re-throw
    /// rather than returning an error result, so the runner can terminate the run.
    /// </returns>
    Task<ToolResult> InvokeAsync(JsonNode input, ToolContext context, CancellationToken ct);
}
