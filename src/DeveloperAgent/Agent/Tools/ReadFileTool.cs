using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeveloperAgent.Agent.Tools;

/// <summary>Reads a UTF-8 text file from within the workspace.</summary>
public sealed class ReadFileTool : ITool
{
    public string Name => "read_file";
    public string Description => "Read the contents of a UTF-8 text file at the given workspace-relative path. Returns the file contents as a string.";

    public JsonNode InputSchema { get; } = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Workspace-relative (or absolute within workspace) path to the file." }
          },
          "required": ["path"]
        }
        """)!;

    public async Task<ToolResult> InvokeAsync(JsonNode input, ToolContext context, CancellationToken ct)
    {
        string? path;
        try
        {
            path = input["path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(path))
                return new ToolResult(true, "Invalid input: 'path' is required.");
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

        if (!File.Exists(resolved))
            return new ToolResult(true, $"File not found: {path}");

        try
        {
            var content = await File.ReadAllTextAsync(resolved, ct);
            return new ToolResult(false, content);
        }
        catch (Exception ex)
        {
            return new ToolResult(true, $"Error reading file: {ex.Message}");
        }
    }
}
