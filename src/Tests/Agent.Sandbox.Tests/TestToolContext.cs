namespace Agent.Sandbox.Tests;

/// <summary>
/// Minimal <see cref="IToolContext"/> double exposing only the workspace root —
/// the single field <see cref="ShellRunTool"/> reads. Replaces the host's concrete
/// <c>ToolContext</c> (which carries policy fields like the GitHub item and run state)
/// so the moved <c>ShellRunTool</c> test stays free of host-policy types.
/// </summary>
internal sealed record TestToolContext(string WorkspaceRoot) : IToolContext;
