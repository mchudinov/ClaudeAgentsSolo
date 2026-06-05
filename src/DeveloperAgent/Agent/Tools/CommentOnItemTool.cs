using System.Text.Json;
using System.Text.Json.Nodes;
using DeveloperAgent.GitHub;

namespace DeveloperAgent.Agent.Tools;

/// <summary>Posts a markdown comment on the active GitHub Project item.</summary>
public sealed class CommentOnItemTool : ITool
{
    private readonly IGitHubProjectService _github;

    /// <summary>Initialises the tool with the GitHub service to delegate to.</summary>
    public CommentOnItemTool(IGitHubProjectService github)
    {
        _github = github;
    }

    public string Name => "comment_on_item";
    public string Description => "Post a markdown comment on the active GitHub Project item. Use for the implementation plan, status updates, or blocker reports.";

    public JsonNode InputSchema { get; } = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "body": { "type": "string", "description": "Markdown content to post as a comment." }
          },
          "required": ["body"]
        }
        """)!;

    public async Task<ToolResult> InvokeAsync(JsonNode input, IToolContext context, CancellationToken ct)
    {
        // Host-policy tool: the runner always supplies the concrete ToolContext, so downcast to
        // reach the GitHub Project item this comment is posted against.
        var ctx = (ToolContext)context;

        string? body;
        try
        {
            body = input["body"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(body))
                return new ToolResult(true, "Invalid input: 'body' is required.");
        }
        catch (Exception ex)
        {
            return new ToolResult(true, $"Invalid input: {ex.Message}");
        }

        try
        {
            await _github.AddItemCommentAsync(ctx.Item.ContentNodeId, body, ct);
            return new ToolResult(false, "{\"commented\":true}");
        }
        catch (Exception ex)
        {
            return new ToolResult(true, $"Failed to post comment: {ex.Message}");
        }
    }
}
