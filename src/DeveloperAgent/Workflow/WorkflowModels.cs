namespace DeveloperAgent.Workflow;

/// <summary>Input passed to every workflow instance and forwarded to each activity.</summary>
public sealed record TaskInput(string ProjectItemId, string ContentNodeId, int ContentNumber, string Title);

/// <summary>Final result produced by <see cref="DeveloperTaskWorkflow"/>.</summary>
/// <param name="Outcome">One of "Done", "Failed", or "Cancelled".</param>
public sealed record TaskResult(string Outcome);
