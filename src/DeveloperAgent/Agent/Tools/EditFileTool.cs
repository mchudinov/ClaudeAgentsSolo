using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeveloperAgent.Agent.Tools;

/// <summary>
/// Replaces exactly one occurrence of <c>old_string</c> with <c>new_string</c> in a workspace file.
/// Fails if the string is absent or appears more than once.
/// </summary>
public sealed class EditFileTool : ITool
{
    public string Name => "edit_file";
    public string Description => "Replace exactly one occurrence of old_string with new_string in a file. Fails if old_string is absent or appears more than once.";

    public JsonNode InputSchema { get; } = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "path":       { "type": "string", "description": "Workspace-relative path to the file to edit." },
            "old_string": { "type": "string", "description": "The exact string to replace. Must appear exactly once in the file." },
            "new_string": { "type": "string", "description": "The string to substitute in place of old_string." }
          },
          "required": ["path", "old_string", "new_string"]
        }
        """)!;

    public async Task<ToolResult> InvokeAsync(JsonNode input, ToolContext context, CancellationToken ct)
    {
        string? path, oldString, newString;
        try
        {
            path = input["path"]?.GetValue<string>();
            oldString = input["old_string"]?.GetValue<string>();
            newString = input["new_string"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(path))
                return new ToolResult(true, "Invalid input: 'path' is required.");
            if (oldString is null)
                return new ToolResult(true, "Invalid input: 'old_string' is required.");
            if (newString is null)
                return new ToolResult(true, "Invalid input: 'new_string' is required.");
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

        string fileContent;
        try
        {
            fileContent = await File.ReadAllTextAsync(resolved, ct);
        }
        catch (Exception ex)
        {
            return new ToolResult(true, $"Error reading file: {ex.Message}");
        }

        int firstIndex = fileContent.IndexOf(oldString, StringComparison.Ordinal);
        if (firstIndex < 0)
            return new ToolResult(true, $"old_string not found in file: {path}");

        // Check uniqueness: search for a second occurrence
        int secondIndex = fileContent.IndexOf(oldString, firstIndex + oldString.Length, StringComparison.Ordinal);
        if (secondIndex >= 0)
            return new ToolResult(true, $"old_string appears more than once in file: {path}");

        string newContent = fileContent.Remove(firstIndex, oldString.Length).Insert(firstIndex, newString);

        try
        {
            await File.WriteAllTextAsync(resolved, newContent, ct);
            return new ToolResult(false, $"{{\"edited\":\"{path}\"}}");
        }
        catch (Exception ex)
        {
            return new ToolResult(true, $"Error writing file: {ex.Message}");
        }
    }
}
