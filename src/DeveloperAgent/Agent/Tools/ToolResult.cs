namespace DeveloperAgent.Agent.Tools;

/// <summary>
/// The result of a tool invocation, serialized back to the model as a <c>tool_result</c> content block.
/// </summary>
/// <param name="IsError">
/// <see langword="true"/> when the tool encountered a recoverable error (e.g. file not found,
/// invalid input). The model receives the error message and can correct itself.
/// </param>
/// <param name="Content">
/// The string payload sent back to the model. On success, typically a JSON string;
/// on error, a human-readable error description.
/// </param>
public sealed record ToolResult(bool IsError, string Content);
