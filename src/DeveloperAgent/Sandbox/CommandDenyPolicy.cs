using Microsoft.Extensions.Options;

namespace DeveloperAgent.Sandbox;

/// <summary>
/// Default <see cref="ICommandDenyPolicy"/>. Evaluates the rules from
/// <see cref="SandboxOptions.DeniedCommands"/> in declaration order and returns
/// the first hit.
/// </summary>
public sealed class CommandDenyPolicy : ICommandDenyPolicy
{
    private readonly IReadOnlyList<CommandDenyRule> _rules;

    public CommandDenyPolicy(IOptions<SandboxOptions> options)
    {
        _rules = options.Value.DeniedCommands ?? [];
    }

    public CommandDenyResult Check(IReadOnlyList<string> argv)
    {
        if (argv is null || argv.Count == 0)
            return CommandDenyResult.Allowed;

        var program = argv[0];

        foreach (var rule in _rules)
        {
            if (!string.Equals(rule.Program, program, StringComparison.Ordinal))
                continue;

            if (!OrderedTokensMatch(argv, rule.ArgPatterns))
                continue;

            if (!SubstringsMatch(argv, rule.ArgContains))
                continue;

            return CommandDenyResult.Denied(
                rule.Name,
                BuildReason(rule, program));
        }

        return CommandDenyResult.Allowed;
    }

    /// <summary>
    /// True when every token in <paramref name="patterns"/> appears in
    /// <c>argv[1..]</c> in the given order. Tokens may be non-contiguous —
    /// <c>["push", "--force"]</c> matches <c>git push --force origin main</c>
    /// and <c>git push origin main --force</c> alike. <see langword="null"/> or
    /// empty patterns trivially match.
    /// </summary>
    private static bool OrderedTokensMatch(IReadOnlyList<string> argv, IReadOnlyList<string>? patterns)
    {
        if (patterns is null || patterns.Count == 0) return true;

        var ai = 1;
        foreach (var token in patterns)
        {
            var found = false;
            while (ai < argv.Count)
            {
                if (string.Equals(argv[ai], token, StringComparison.Ordinal))
                {
                    found = true;
                    ai++;
                    break;
                }
                ai++;
            }
            if (!found) return false;
        }
        return true;
    }

    /// <summary>
    /// True when every substring in <paramref name="substrings"/> appears in
    /// at least one element of <c>argv[1..]</c>. <see langword="null"/> or
    /// empty substrings trivially match.
    /// </summary>
    private static bool SubstringsMatch(IReadOnlyList<string> argv, IReadOnlyList<string>? substrings)
    {
        if (substrings is null || substrings.Count == 0) return true;

        foreach (var needle in substrings)
        {
            var hit = false;
            for (var i = 1; i < argv.Count; i++)
            {
                if (argv[i].Contains(needle, StringComparison.Ordinal))
                {
                    hit = true;
                    break;
                }
            }
            if (!hit) return false;
        }
        return true;
    }

    private static string BuildReason(CommandDenyRule rule, string program)
    {
        var args = rule.ArgPatterns is { Count: > 0 } ? " " + string.Join(' ', rule.ArgPatterns) : string.Empty;
        return $"command '{program}{args}' is denied by sandbox policy: deny rule '{rule.Name}'";
    }
}
