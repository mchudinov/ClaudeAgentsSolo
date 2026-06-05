namespace Agent.Tools.Tests;

/// <summary>Minimal <see cref="IToolContext"/> for exercising the generic file tools.</summary>
internal sealed record TestToolContext(string WorkspaceRoot) : IToolContext;
