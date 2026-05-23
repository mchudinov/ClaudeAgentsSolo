namespace DeveloperAgent.Sandbox;

/// <summary>
/// Outcome of a <see cref="ICommandDenyPolicy.Check(System.Collections.Generic.IReadOnlyList{string})"/>
/// evaluation.
/// </summary>
/// <param name="IsDenied">
/// <see langword="true"/> when the argv hits at least one deny rule.
/// </param>
/// <param name="RuleName">
/// Name of the first rule that matched, e.g. <c>no-wget</c>.
/// <see langword="null"/> on allow.
/// </param>
/// <param name="Reason">
/// Human-readable explanation suitable for inclusion in
/// <see cref="Workspace.SandboxViolationException"/>. <see langword="null"/> on allow.
/// </param>
public readonly record struct CommandDenyResult(bool IsDenied, string? RuleName, string? Reason)
{
    public static CommandDenyResult Allowed { get; } = new(false, null, null);
    public static CommandDenyResult Denied(string ruleName, string reason) => new(true, ruleName, reason);
}

/// <summary>
/// Command-deny gate consulted by <see cref="Workspace.ICommandSandbox"/> BEFORE
/// the workspace allowlist. A rule hit short-circuits the sandbox — deny wins
/// over allow, so a command can be both on the allowlist and still rejected by
/// this policy (e.g. <c>git push --force</c> when <c>git push</c> is allowed).
/// </summary>
public interface ICommandDenyPolicy
{
    /// <summary>
    /// Evaluates the deny rules against <paramref name="argv"/>.
    /// </summary>
    /// <param name="argv">
    /// Tokenised command line, <c>argv[0]</c> = executable. The caller is
    /// expected to have already stripped any leading <c>-c key=value</c> pairs.
    /// </param>
    CommandDenyResult Check(IReadOnlyList<string> argv);
}
