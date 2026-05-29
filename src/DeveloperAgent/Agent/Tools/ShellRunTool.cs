using System.Text.Json;
using System.Text.Json.Nodes;
using DeveloperAgent.Workspace;

namespace DeveloperAgent.Agent.Tools;

/// <summary>
/// Runs a sandboxed shell command in a workspace-relative working directory.
/// Delegates to <see cref="ICommandSandbox"/>. Re-throws <see cref="SandboxViolationException"/>
/// so the runner can end the run with <see cref="Agent.AgentRunOutcome.SandboxViolation"/>.
/// </summary>
public sealed class ShellRunTool : ITool
{
    private const int DefaultTimeoutSeconds = 600;
    private const int MinTimeoutSeconds = 5;
    private const int MaxTimeoutSeconds = 1200;

    private readonly ICommandSandbox _sandbox;

    /// <summary>Initialises the tool with the sandbox to delegate to.</summary>
    public ShellRunTool(ICommandSandbox sandbox)
    {
        _sandbox = sandbox;
    }

    public string Name => "shell_run";
    public string Description => "Run a sandboxed shell command in a workspace directory. Returns exit_code, stdout, stderr, elapsed_seconds, and timed_out. Command must be in the workspace allowlist.";

    public JsonNode InputSchema { get; } = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "command":           { "type": "string",  "description": "The command line to execute (must be in the allowlist)." },
            "working_directory": { "type": "string",  "description": "Workspace-relative working directory. Defaults to repo root." },
            "timeout_seconds":   { "type": "integer", "description": "Timeout in seconds (5–1200). Default 600." }
          },
          "required": ["command"]
        }
        """)!;

    public async Task<ToolResult> InvokeAsync(JsonNode input, ToolContext context, CancellationToken ct)
    {
        string? command;
        string? workingDirectory;
        int timeoutSeconds;
        try
        {
            command = input["command"]?.GetValue<string>();
            workingDirectory = input["working_directory"]?.GetValue<string>();
            timeoutSeconds = input["timeout_seconds"]?.GetValue<int>() ?? DefaultTimeoutSeconds;
            if (string.IsNullOrWhiteSpace(command))
                return new ToolResult(true, "Invalid input: 'command' is required.");
            timeoutSeconds = Math.Clamp(timeoutSeconds, MinTimeoutSeconds, MaxTimeoutSeconds);
        }
        catch (Exception ex)
        {
            return new ToolResult(true, $"Invalid input: {ex.Message}");
        }

        // Resolve working directory (default = repo root)
        string resolvedCwd;
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            resolvedCwd = Path.GetFullPath(context.Workspace.RepoRoot);
        }
        else
        {
            try
            {
                resolvedCwd = PathValidator.ResolveOrThrow(workingDirectory, context.Workspace);
            }
            catch (InvalidOperationException ex)
            {
                return new ToolResult(true, ex.Message);
            }
        }

        // SandboxViolationException propagates up intentionally — runner handles it
        CommandResult result;
        try
        {
            // Request container isolation (isolate: true). The sandbox runs the command
            // inside an isolated child container when ContainerRuntime.Enabled is set;
            // otherwise it falls back to direct host execution. Allowlist / deny / cwd
            // validation runs identically either way.
            result = await _sandbox.RunAsync(command, resolvedCwd, TimeSpan.FromSeconds(timeoutSeconds), ct, isolate: true);
        }
        catch (SandboxViolationException)
        {
            throw; // runner catches this and ends with SandboxViolation outcome
        }
        catch (Exception ex)
        {
            return new ToolResult(true, $"Command execution error: {ex.Message}");
        }

        var payload = new
        {
            exit_code = result.ExitCode,
            stdout = result.Stdout,
            stderr = result.Stderr,
            elapsed_seconds = (int)result.Elapsed.TotalSeconds,
            timed_out = result.TimedOut,
        };
        return new ToolResult(false, JsonSerializer.Serialize(payload));
    }
}
