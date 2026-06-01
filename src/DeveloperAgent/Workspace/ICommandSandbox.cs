namespace DeveloperAgent.Workspace;

/// <summary>
/// Executes an external command line under the allowlist and working-directory
/// constraints defined in <see cref="DeveloperAgent.Configuration.WorkspaceOptions"/>.
/// </summary>
/// <remarks>
/// A command line may chain several commands with the top-level connectors
/// <c>&amp;&amp;</c>, <c>||</c> and <c>;</c>. Each segment is tokenised into an argv
/// array and validated against the allowlist / deny policy independently, and the
/// whole line is rejected if ANY segment is disallowed (fail-closed). There is still
/// no real shell: each segment runs as a direct child-process exec, so connectors
/// inside quotes, a lone <c>|</c>, redirections, and globbing are treated as literal
/// arguments — never expanded.
/// </remarks>
public interface ICommandSandbox
{
    /// <summary>
    /// Runs a command after verifying it against the allowlist and the workspace
    /// working-directory constraint.
    /// </summary>
    /// <param name="commandLine">
    /// The command and its arguments as a single string, e.g.
    /// <c>"dotnet build src/Foo.csproj --no-restore"</c>. Quoted tokens are honoured.
    /// </param>
    /// <param name="workingDirectory">
    /// Absolute path that must be inside <see cref="DeveloperAgent.Configuration.WorkspaceOptions.RootPath"/>.
    /// </param>
    /// <param name="timeout">Maximum wall-clock time before the child process is killed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="isolate">
    /// When <see langword="true"/> and container isolation is enabled
    /// (<see cref="DeveloperAgent.Sandbox.ContainerRuntimeOptions.Enabled"/>), the command
    /// runs inside an isolated child container with the workspace bind-mounted read-write
    /// and the rest of the host read-only. Allowlist / deny / working-directory validation
    /// runs identically regardless of this flag — isolation is defence-in-depth, not a
    /// replacement for the allowlist. Callers that need host network and host state (e.g.
    /// git clone/push) leave this <see langword="false"/> (the default).
    /// </param>
    /// <returns>A <see cref="CommandResult"/> with stdout, stderr, exit code, elapsed time, and timeout flag.</returns>
    /// <exception cref="SandboxViolationException">
    /// Thrown when the command is not in the allowlist or the working directory is
    /// outside the workspace root.
    /// </exception>
    Task<CommandResult> RunAsync(
        string commandLine,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken ct,
        bool isolate = false);
}
