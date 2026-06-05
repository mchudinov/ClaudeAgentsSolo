namespace Agent.Tools;

/// <summary>
/// Classification of a tool error. Allows callers (and the model) to react
/// differently to recoverable validation errors versus sandbox denials.
/// </summary>
public enum ToolErrorKind
{
    /// <summary>The tool refused the request because a sandbox deny rule fired.</summary>
    Denied,
}

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
/// <param name="ErrorKind">
/// Optional classification of the error. <see langword="null"/> on success and on
/// generic recoverable errors. Set to <see cref="ToolErrorKind.Denied"/> when a
/// sandbox path-deny rule rejected the call.
/// </param>
public sealed record ToolResult(bool IsError, string Content, ToolErrorKind? ErrorKind = null)
{
    /// <summary>
    /// Builds a <see cref="ToolResult"/> representing a sandbox denial.
    /// </summary>
    /// <param name="reason">The human-readable reason returned to the model.</param>
    public static ToolResult Denied(string reason) => new(true, reason, ToolErrorKind.Denied);
}
