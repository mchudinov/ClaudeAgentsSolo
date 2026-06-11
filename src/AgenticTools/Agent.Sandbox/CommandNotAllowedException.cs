namespace Agent.Sandbox;

/// <summary>
/// Thrown when <see cref="ICommandSandbox"/> rejects a command segment because it is not in the
/// workspace allowlist — as opposed to hitting an explicit <em>deny</em> rule
/// (<see cref="CommandDeniedException"/>).
/// <para>
/// Unlike <see cref="SandboxViolationException"/>, this is <b>recoverable</b>: the command never
/// ran (the allowlist is checked before execution), so <see cref="ShellRunTool"/> returns it to the
/// model as a tool error (carrying the list of allowed commands) and the run continues, letting the
/// model retry with an allowed command. A deny-rule hit is likewise recoverable (it surfaces as
/// <see cref="CommandDeniedException"/>); only a workspace escape (cwd outside the root) or an
/// empty/malformed segment stays a fatal <see cref="SandboxViolationException"/> that terminates the
/// run. These recoverable types do <b>not</b> derive from <see cref="SandboxViolationException"/>,
/// so the host's run-terminating latch (which keys on it) never treats them as fatal.
/// </para>
/// </summary>
public sealed class CommandNotAllowedException : Exception
{
    /// <inheritdoc />
    public CommandNotAllowedException(string message) : base(message) { }

    /// <inheritdoc />
    public CommandNotAllowedException(string message, Exception innerException)
        : base(message, innerException) { }
}
