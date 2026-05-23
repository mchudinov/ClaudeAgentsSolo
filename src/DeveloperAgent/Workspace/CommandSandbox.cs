using DeveloperAgent.Configuration;
using DeveloperAgent.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace DeveloperAgent.Workspace;

/// <summary>
/// Production implementation of <see cref="ICommandSandbox"/>.
/// Validates the allowlist and working-directory constraints before delegating to
/// <see cref="IProcessRunner"/>.
/// </summary>
public sealed class CommandSandbox : ICommandSandbox
{
    private readonly IProcessRunner _runner;
    private readonly IOptions<WorkspaceOptions> _options;
    private readonly IPathDenyPolicy _pathDenyPolicy;
    private readonly ILogger<CommandSandbox> _logger;

    /// <summary>
    /// Initialises a new <see cref="CommandSandbox"/>.
    /// The constructor is <c>internal</c> because <see cref="IProcessRunner"/> is an
    /// internal abstraction — consumers resolve <see cref="ICommandSandbox"/> from DI.
    /// </summary>
    internal CommandSandbox(
        IProcessRunner runner,
        IOptions<WorkspaceOptions> options,
        IPathDenyPolicy pathDenyPolicy,
        ILogger<CommandSandbox> logger)
    {
        _runner = runner;
        _options = options;
        _pathDenyPolicy = pathDenyPolicy;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CommandResult> RunAsync(
        string commandLine,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var opts = _options.Value;

        // ── 1. Validate CWD ──────────────────────────────────────────────────
        ValidateCwd(workingDirectory, opts.RootPath, _pathDenyPolicy);

        // ── 2. Tokenise command line ─────────────────────────────────────────
        var argv = Tokenise(commandLine);
        if (argv.Count == 0)
            throw new SandboxViolationException("Empty command line.");

        // ── 3. Strip leading -c <kv> pairs (git auth header pattern) ────────
        var strippedArgv = StripLeadingConfigPairs(argv);

        // ── 4. Prefix-match against allowlist ───────────────────────────────
        var matchedEntry = FindAllowlistEntry(strippedArgv, opts.AllowedCommands);
        if (matchedEntry is null)
            throw new SandboxViolationException(
                $"Command '{commandLine}' does not match any entry in AllowedCommands.");

        // ── 5. Special-case: block git push --force / -f ─────────────────────
        if (matchedEntry == "git push")
        {
            foreach (var arg in strippedArgv.Skip(2)) // skip "git" "push"
            {
                if (arg is "--force" or "-f")
                    throw new SandboxViolationException(
                        "git push --force is not permitted.");
            }
        }

        // ── 6. Log invocation (no secret values) ─────────────────────────────
        var outputHash = ComputeInvocationId(commandLine);
        _logger.LogInformation(
            "Sandbox: running {Executable} with {ArgCount} args in {WorkingDirectory} (id={InvocationId})",
            argv[0], argv.Count - 1, workingDirectory, outputHash);

        // ── 7. Execute ───────────────────────────────────────────────────────
        var result = await _runner.RunAsync(
            argv[0],
            argv.Skip(1).ToArray(),
            workingDirectory,
            timeout,
            ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Sandbox: {Executable} exited {ExitCode} in {Elapsed:N0}ms TimedOut={TimedOut} (id={InvocationId})",
            argv[0], result.ExitCode, result.Elapsed.TotalMilliseconds, result.TimedOut, outputHash);

        return new CommandResult(
            result.ExitCode,
            result.Stdout,
            result.Stderr,
            result.Elapsed,
            result.TimedOut);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void ValidateCwd(string cwd, string rootPath, IPathDenyPolicy denyPolicy)
    {
        // Resolve absolute CWD up-front so the deny policy sees a canonical form.
        var absoluteCwd = Path.GetFullPath(cwd);

        var deny = denyPolicy.Check(absoluteCwd, rootPath);
        if (deny.IsDenied)
        {
            // Preserve the legacy message contract so callers / tests that match on
            // "cwd outside workspace root" keep working for the workspace-escape case.
            var prefix = (deny.Reason ?? string.Empty).StartsWith("path escapes workspace")
                ? $"cwd outside workspace root: '{cwd}' is not under '{rootPath}'."
                : $"cwd denied by sandbox: {deny.Reason}";
            throw new SandboxViolationException(prefix);
        }
    }

    private static string? FindAllowlistEntry(
        IReadOnlyList<string> argv,
        IReadOnlyList<string> allowedCommands)
    {
        foreach (var entry in allowedCommands)
        {
            var entryTokens = Tokenise(entry);
            if (entryTokens.Count == 0) continue;
            if (entryTokens.Count > argv.Count) continue;

            var match = true;
            for (var i = 0; i < entryTokens.Count; i++)
            {
                if (!string.Equals(entryTokens[i], argv[i], StringComparison.Ordinal))
                {
                    match = false;
                    break;
                }
            }
            if (match) return entry;
        }
        return null;
    }

    /// <summary>
    /// Strips leading <c>-c key=value</c> pairs from <paramref name="argv"/> used by
    /// the git auth-header pattern: <c>git -c http.extraheader="…" clone …</c>.
    /// Only <em>leading</em> pairs are stripped — a <c>-c</c> appearing after a
    /// non-<c>-c</c> token is treated as a normal argument.
    /// </summary>
    internal static IReadOnlyList<string> StripLeadingConfigPairs(IReadOnlyList<string> argv)
    {
        // argv[0] is the executable (e.g. "git") — keep it, start stripping from index 1
        if (argv.Count < 2) return argv;

        var result = new List<string> { argv[0] };
        var i = 1;
        while (i + 1 < argv.Count
               && argv[i] == "-c"
               && argv[i + 1].Contains('='))
        {
            // Skip this -c <kv> pair
            i += 2;
        }
        for (; i < argv.Count; i++)
            result.Add(argv[i]);

        return result;
    }

    /// <summary>
    /// Simple tokeniser: splits on whitespace, honouring single and double quotes.
    /// </summary>
    internal static IReadOnlyList<string> Tokenise(string commandLine)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char? inQuote = null;

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];

            if (inQuote.HasValue)
            {
                if (c == inQuote.Value)
                    inQuote = null;
                else
                    current.Append(c);
            }
            else if (c is '"' or '\'')
            {
                inQuote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }

    private static string ComputeInvocationId(string commandLine)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(commandLine));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }
}
