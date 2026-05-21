namespace DeveloperAgent.Workspace;

/// <summary>
/// Thrown when <see cref="ICommandSandbox"/> rejects a command because it violates
/// the allowlist or the working-directory constraint.
/// The lifecycle loop catches this exception, posts a comment on the project item
/// (without the offending command), and moves the item back to Ready.
/// </summary>
public sealed class SandboxViolationException : Exception
{
    /// <inheritdoc />
    public SandboxViolationException(string message) : base(message) { }

    /// <inheritdoc />
    public SandboxViolationException(string message, Exception innerException)
        : base(message, innerException) { }
}
