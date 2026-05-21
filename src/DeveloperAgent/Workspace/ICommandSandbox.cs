namespace DeveloperAgent.Workspace;

/// <summary>
/// Executes a single external command under the allowlist and working-directory
/// constraints defined in <see cref="DeveloperAgent.Configuration.WorkspaceOptions"/>.
/// </summary>
/// <remarks>
/// Commands are tokenised into an argv array before execution — there is no shell
/// interpretation. <c>&amp;&amp;</c>, <c>||</c>, redirections, and globbing are
/// treated as literal arguments.
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
    /// <returns>A <see cref="CommandResult"/> with stdout, stderr, exit code, elapsed time, and timeout flag.</returns>
    /// <exception cref="SandboxViolationException">
    /// Thrown when the command is not in the allowlist or the working directory is
    /// outside the workspace root.
    /// </exception>
    Task<CommandResult> RunAsync(
        string commandLine,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken ct);
}
