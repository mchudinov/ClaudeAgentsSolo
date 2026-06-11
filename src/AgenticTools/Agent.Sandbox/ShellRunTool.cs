using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Tools;
using Microsoft.Extensions.Options;

namespace Agent.Sandbox;

/// <summary>
/// Runs a sandboxed shell command in a workspace-relative working directory.
/// Delegates to <see cref="ICommandSandbox"/>. The two recoverable rejections — an allowlist miss
/// (<see cref="CommandNotAllowedException"/>) and a deny-rule hit (<see cref="CommandDeniedException"/>)
/// — are returned to the model as tool errors so the run continues; only a workspace escape
/// (<see cref="SandboxViolationException"/>) is re-thrown so the host runner ends the run.
/// </summary>
public sealed class ShellRunTool : ITool
{
    private const int DefaultTimeoutSeconds = 600;
    private const int MinTimeoutSeconds = 5;
    private const int MaxTimeoutSeconds = 1200;

    private readonly ICommandSandbox _sandbox;
    private readonly string _description;

    /// <summary>Initialises the tool with the sandbox to delegate to.</summary>
    /// <param name="sandbox">The command sandbox commands are validated and executed through.</param>
    /// <param name="workspaceOptions">
    /// Supplies the allowlist enumerated in <see cref="Description"/> so the model is told which
    /// commands it may run up-front (rather than guessing — a guessed, un-listed command is what
    /// used to kill runs). An un-listed or denied command is rejected without executing and the
    /// error is returned to the model (allowlist miss or deny-rule hit alike); only a workspace
    /// escape ends the run.
    /// </param>
    public ShellRunTool(ICommandSandbox sandbox, IOptions<WorkspaceOptions> workspaceOptions)
    {
        _sandbox = sandbox;
        var allowed = workspaceOptions.Value.AllowedCommands;
        _description =
            "Run a sandboxed shell command in a workspace directory. Returns exit_code, stdout, " +
            "stderr, elapsed_seconds, and timed_out. Only commands in the workspace allowlist may " +
            "run — any other command returns an error without executing. " +
            $"Allowed commands: {string.Join(", ", allowed)}.";
    }

    public string Name => "shell_run";
    public string Description => _description;

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

    public async Task<ToolResult> InvokeAsync(JsonNode input, IToolContext context, CancellationToken ct)
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
            resolvedCwd = Path.GetFullPath(context.WorkspaceRoot);
        }
        else
        {
            try
            {
                resolvedCwd = PathValidator.ResolveOrThrow(workingDirectory, context.WorkspaceRoot);
            }
            catch (InvalidOperationException ex)
            {
                return new ToolResult(true, ex.Message);
            }
        }

        // A workspace escape (SandboxViolationException) propagates up intentionally — the runner
        // ends the run. The two recoverable rejections — an allowlist miss
        // (CommandNotAllowedException) and a deny-rule hit (CommandDeniedException) — are handled
        // below: the command never ran, so they are returned to the model rather than ending the run.
        CommandResult result;
        try
        {
            // Request container isolation (isolate: true). The sandbox runs the command
            // inside an isolated child container when ContainerRuntime.Enabled is set;
            // otherwise it falls back to direct host execution. Allowlist / deny / cwd
            // validation runs identically either way.
            result = await _sandbox.RunAsync(command, resolvedCwd, TimeSpan.FromSeconds(timeoutSeconds), ct, isolate: true);
        }
        catch (CommandNotAllowedException ex)
        {
            // Recoverable: the command was not on the allowlist and never ran. Hand the error —
            // which lists the allowed commands — back to the model so it retries with an allowed one.
            return new ToolResult(true, ex.Message);
        }
        catch (CommandDeniedException ex)
        {
            // Recoverable: the command matched a deny rule (blind force-push, curl, secret
            // manipulation, …) and never ran. Hand the deny reason back to the model so it can
            // adapt (e.g. use the allowed --force-with-lease) instead of ending the whole run.
            return new ToolResult(true, ex.Message);
        }
        catch (SandboxViolationException)
        {
            throw; // workspace escape / malformed segment — runner ends the run with SandboxViolation outcome
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
