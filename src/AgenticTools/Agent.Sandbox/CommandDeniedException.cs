namespace Agent.Sandbox;

/// <summary>
/// Thrown when <see cref="ICommandSandbox"/> rejects a command segment because it matched an
/// explicit <em>deny</em> rule (curl, blind <c>git push --force</c>/<c>-f</c>, secret/branch-protection
/// mutation, …) — as opposed to merely not being on the workspace allowlist
/// (<see cref="CommandNotAllowedException"/>).
/// <para>
/// Like <see cref="CommandNotAllowedException"/>, and unlike <see cref="SandboxViolationException"/>,
/// this is <b>recoverable</b>: the deny policy runs before execution, so the command never ran. The
/// security boundary is therefore intact either way — making this recoverable changes only what
/// happens <em>after</em> the block. <see cref="ShellRunTool"/> returns it to the model as a tool
/// error (carrying the deny reason) so the model can adapt — e.g. drop the denied flag, or use the
/// safe <c>--force-with-lease</c> instead of blind <c>--force</c> — rather than the whole run being
/// terminated. Only a workspace-escape (cwd outside the root) or an empty/malformed segment stays a
/// fatal <see cref="SandboxViolationException"/>. This type does <b>not</b> derive from
/// <see cref="SandboxViolationException"/>, so the host's run-terminating latch (which keys on
/// <see cref="SandboxViolationException"/>) never treats a deny-rule hit as fatal.
/// </para>
/// </summary>
public sealed class CommandDeniedException : Exception
{
    /// <inheritdoc />
    public CommandDeniedException(string message) : base(message) { }

    /// <inheritdoc />
    public CommandDeniedException(string message, Exception innerException)
        : base(message, innerException) { }
}
