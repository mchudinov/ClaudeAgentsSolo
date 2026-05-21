using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeveloperAgent.Agent.Tools;

/// <summary>Lists entries under a workspace-relative directory path.</summary>
public sealed class ListDirectoryTool : ITool
{
    public string Name => "list_directory";
    public string Description => "List files and directories under a workspace-relative path. Supports optional recursive enumeration and glob pattern filtering.";

    public JsonNode InputSchema { get; } = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "path":      { "type": "string",  "description": "Workspace-relative path of the directory to list." },
            "recursive": { "type": "boolean", "description": "If true, recurse into subdirectories. Default false." },
            "glob":      { "type": "string",  "description": "Optional glob pattern to filter entries (e.g. '*.cs'). Null means all entries." }
          },
          "required": ["path"]
        }
        """)!;

    public Task<ToolResult> InvokeAsync(JsonNode input, ToolContext context, CancellationToken ct)
    {
        string? path;
        bool recursive;
        string? glob;
        try
        {
            path = input["path"]?.GetValue<string>();
            recursive = input["recursive"]?.GetValue<bool>() ?? false;
            glob = input["glob"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(path))
                return Task.FromResult(new ToolResult(true, "Invalid input: 'path' is required."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolResult(true, $"Invalid input: {ex.Message}"));
        }

        string resolved;
        try
        {
            resolved = PathValidator.ResolveOrThrow(path, context.Workspace);
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(new ToolResult(true, ex.Message));
        }

        if (!Directory.Exists(resolved))
            return Task.FromResult(new ToolResult(true, $"Directory not found: {path}"));

        try
        {
            var searchOption = recursive
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;

            var pattern = glob ?? "*";
            var entries = Directory.EnumerateFileSystemEntries(resolved, pattern, searchOption);

            string repoRoot = Path.GetFullPath(context.Workspace.RepoRoot);

            var items = entries.Select(e =>
            {
                bool isDir = Directory.Exists(e);
                string rel = Path.GetRelativePath(repoRoot, e).Replace('\\', '/');
                return new { path = rel, kind = isDir ? "directory" : "file" };
            }).ToList();

            var json = JsonSerializer.Serialize(items);
            return Task.FromResult(new ToolResult(false, json));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolResult(true, $"Error listing directory: {ex.Message}"));
        }
    }
}
