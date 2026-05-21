using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeveloperAgent.Agent.Tools;

/// <summary>Creates or overwrites a file within the workspace, creating parent directories as needed.</summary>
public sealed class WriteFileTool : ITool
{
    public string Name => "write_file";
    public string Description => "Create or overwrite a file at the given workspace-relative path with the specified content. Parent directories are created automatically.";

    public JsonNode InputSchema { get; } = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "path":    { "type": "string", "description": "Workspace-relative path to the file to write." },
            "content": { "type": "string", "description": "UTF-8 content to write to the file." }
          },
          "required": ["path", "content"]
        }
        """)!;

    public async Task<ToolResult> InvokeAsync(JsonNode input, ToolContext context, CancellationToken ct)
    {
        string? path;
        string? content;
        try
        {
            path = input["path"]?.GetValue<string>();
            content = input["content"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(path))
                return new ToolResult(true, "Invalid input: 'path' is required.");
            if (content is null)
                return new ToolResult(true, "Invalid input: 'content' is required.");
        }
        catch (Exception ex)
        {
            return new ToolResult(true, $"Invalid input: {ex.Message}");
        }

        string resolved;
        try
        {
            resolved = PathValidator.ResolveOrThrow(path, context.Workspace);
        }
        catch (InvalidOperationException ex)
        {
            return new ToolResult(true, ex.Message);
        }

        try
        {
            var dir = Path.GetDirectoryName(resolved);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(resolved, content, ct);
            return new ToolResult(false, $"{{\"written\":\"{path}\"}}");
        }
        catch (Exception ex)
        {
            return new ToolResult(true, $"Error writing file: {ex.Message}");
        }
    }
}
