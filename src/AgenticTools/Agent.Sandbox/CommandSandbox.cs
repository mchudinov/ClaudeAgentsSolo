using Agent.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace Agent.Sandbox;

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
    private readonly ICommandDenyPolicy _commandDenyPolicy;
    private readonly ILogger<CommandSandbox> _logger;
    private readonly IContainerRuntime? _containerRuntime;
    private readonly ContainerRuntimeOptions _containerOptions;

    /// <summary>
    /// Initialises a new <see cref="CommandSandbox"/>.
    /// The constructor is <c>internal</c> because <see cref="IProcessRunner"/> is an
    /// internal abstraction — consumers resolve <see cref="ICommandSandbox"/> from DI.
    /// </summary>
    /// <param name="containerRuntime">
    /// Optional container runtime used when a caller requests isolation
    /// (<c>isolate: true</c>) and <see cref="ContainerRuntimeOptions.Enabled"/> is set.
    /// Null disables container isolation entirely — every command runs on the host.
    /// </param>
    /// <param name="containerOptions">
    /// Container-runtime configuration. Null uses defaults (isolation disabled).
    /// </param>
    internal CommandSandbox(
        IProcessRunner runner,
        IOptions<WorkspaceOptions> options,
        IPathDenyPolicy pathDenyPolicy,
        ICommandDenyPolicy commandDenyPolicy,
        ILogger<CommandSandbox> logger,
        IContainerRuntime? containerRuntime = null,
        IOptions<ContainerRuntimeOptions>? containerOptions = null)
    {
        _runner = runner;
        _options = options;
        _pathDenyPolicy = pathDenyPolicy;
        _commandDenyPolicy = commandDenyPolicy;
        _logger = logger;
        _containerRuntime = containerRuntime;
        _containerOptions = containerOptions?.Value ?? new ContainerRuntimeOptions();
    }

    /// <inheritdoc />
    public async Task<CommandResult> RunAsync(
        string commandLine,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken ct,
        bool isolate = false)
    {
        var opts = _options.Value;

        // ── 1. Validate CWD (once for the whole command line) ────────────────
        ValidateCwd(workingDirectory, opts.RootPath, _pathDenyPolicy);

        // ── 2. Split into top-level segments on &&, ||, ; ────────────────────
        // No real shell is ever invoked — each segment runs as a direct exec via
        // IProcessRunner. We recognise only these three connectors so multi-step
        // inspection commands (e.g. "pwd && git status && git branch") work; every
        // other metacharacter (single |, redirects, $(), globs) stays a literal arg.
        var segments = SplitTopLevel(commandLine);

        // ── 3. Validate EVERY segment BEFORE running any (fail-closed) ───────
        // The deny policy runs BEFORE the allowlist (deny wins) for each segment.
        // Because validation is a separate pass, a compound line is rejected in full
        // if ANY segment is disallowed/denied — "git status && rm -rf /" never runs
        // its allowed prefix. Either way the command never executes, so the security
        // boundary is identical; the rejection CLASS only controls what happens after.
        //
        // Two REJECTION classes are RECOVERABLE — neither ends the run; both are handed
        // back to the model by ShellRunTool so it can adapt:
        //   • A deny-rule hit (curl, blind force-push, secret manipulation, …) throws
        //     CommandDeniedException, carrying the deny reason.
        //   • A plain allowlist MISS throws CommandNotAllowedException, carrying the allowlist.
        // Only a workspace escape (cwd outside the root, handled in ValidateCwd) or an
        // empty/malformed segment is FATAL — a SandboxViolationException the host runner turns
        // into a run-terminating SandboxViolation outcome.
        // Deny wins GLOBALLY: every segment's deny policy is evaluated (throwing immediately)
        // before any recoverable miss is surfaced, so a denied segment anywhere surfaces the
        // more-actionable deny message even when an earlier segment is merely off the allowlist.
        var validated = new List<(string Connector, IReadOnlyList<string> Argv)>(segments.Count);
        CommandNotAllowedException? deferredMiss = null;
        foreach (var segment in segments)
        {
            // Tokenise, then strip leading -c <kv> pairs (git auth header pattern).
            var argv = Tokenise(segment.Text);
            if (argv.Count == 0)
                throw new SandboxViolationException(
                    $"Empty command segment in '{commandLine}'.");

            var strippedArgv = StripLeadingConfigPairs(argv);

            var denyResult = _commandDenyPolicy.Check(strippedArgv);
            if (denyResult.IsDenied)
                throw new CommandDeniedException(
                    denyResult.Reason ?? $"command is denied by sandbox policy: rule '{denyResult.RuleName}'");

            // Remember the FIRST allowlist miss but keep scanning the remaining segments for a
            // deny that would override it (deny wins globally). Surfaced after the loop.
            if (deferredMiss is null && FindAllowlistEntry(strippedArgv, opts.AllowedCommands) is null)
                deferredMiss = new CommandNotAllowedException(
                    $"Command '{segment.Text}' is not in the workspace allowlist. " +
                    $"Allowed commands: {string.Join(", ", opts.AllowedCommands)}. " +
                    "Re-run using only an allowed command.");

            // Execution uses the ORIGINAL argv (with the -c auth pair intact); only
            // the allowlist/deny checks see the stripped form.
            validated.Add((segment.Connector, argv));
        }

        if (deferredMiss is not null)
            throw deferredMiss;

        // ── 4. Execute segments sequentially honouring connector semantics ───
        // The first segment always runs; "&&" runs only after success, "||" only
        // after failure, ";" always. A timeout ends the chain (the wall-clock
        // budget is spent). Results are aggregated into a single CommandResult.
        var executed = new List<CommandResult>(validated.Count);
        CommandResult? previous = null;
        foreach (var (connector, argv) in validated)
        {
            if (!ShouldRunSegment(connector, previous))
                continue;

            var segmentResult = await ExecuteSegmentAsync(
                argv, workingDirectory, timeout, isolate, opts, ct).ConfigureAwait(false);

            executed.Add(segmentResult);
            previous = segmentResult;

            if (segmentResult.TimedOut)
                break;
        }

        return AggregateResults(executed);
    }

    /// <summary>
    /// Decides whether a segment runs, given the connector preceding it and the
    /// result of the previously executed segment. The first segment (empty
    /// connector) and <c>;</c> always run; <c>&amp;&amp;</c> runs only when the
    /// previous segment exited 0 and did not time out; <c>||</c> runs only when the
    /// previous segment failed (non-zero exit).
    /// </summary>
    private static bool ShouldRunSegment(string connector, CommandResult? previous) => connector switch
    {
        "&&" => previous is { ExitCode: 0, TimedOut: false },
        "||" => previous is null || previous.ExitCode != 0,
        _ => true, // first segment ("") and ";" always run
    };

    /// <summary>
    /// Combines the results of the executed segments into a single
    /// <see cref="CommandResult"/>: stdout/stderr are concatenated in execution
    /// order, the exit code is that of the LAST executed segment (shell <c>$?</c>
    /// semantics), elapsed time is summed, and TimedOut is true if any segment
    /// timed out. The first segment always runs, so <paramref name="executed"/> is
    /// never empty; the single-segment case returns its result unchanged.
    /// </summary>
    private static CommandResult AggregateResults(IReadOnlyList<CommandResult> executed)
    {
        if (executed.Count == 1)
            return executed[0];

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var elapsed = TimeSpan.Zero;
        var timedOut = false;
        foreach (var r in executed)
        {
            stdout.Append(r.Stdout);
            stderr.Append(r.Stderr);
            elapsed += r.Elapsed;
            timedOut |= r.TimedOut;
        }

        return new CommandResult(
            executed[^1].ExitCode,
            stdout.ToString(),
            stderr.ToString(),
            elapsed,
            timedOut);
    }

    /// <summary>
    /// Logs and executes a single, already-validated segment — either inside an
    /// isolated child container (when <paramref name="isolate"/> is requested and
    /// container isolation is enabled) or as a direct host child process.
    /// </summary>
    private async Task<CommandResult> ExecuteSegmentAsync(
        IReadOnlyList<string> argv,
        string workingDirectory,
        TimeSpan timeout,
        bool isolate,
        WorkspaceOptions opts,
        CancellationToken ct)
    {
        // Log invocation (no secret values — argv only, never the environment).
        var outputHash = ComputeInvocationId(string.Join(' ', argv));
        _logger.LogInformation(
            "Sandbox: running {Executable} with {ArgCount} args in {WorkingDirectory} (id={InvocationId})",
            argv[0], argv.Count - 1, workingDirectory, outputHash);

        // Container isolation is opt-in per call AND gated by config. When a caller
        // requests it (shell_run) and ContainerRuntime.Enabled is set with a runtime
        // available, the command runs inside an isolated child container; otherwise it
        // runs on the host (git clone/push and all callers when isolation is disabled).
        if (isolate && _containerOptions.Enabled && _containerRuntime is not null)
        {
            var request = new ContainerRunRequest(
                Executable: argv[0],
                Arguments: argv.Skip(1).ToArray(),
                WorkspacePath: opts.RootPath,
                MountPath: _containerOptions.MountPath,
                WorkingDirectoryInContainer: MapToContainerPath(
                    workingDirectory, opts.RootPath, _containerOptions.MountPath),
                Timeout: timeout);

            var containerResult = await _containerRuntime
                .RunInContainerAsync(request, ct)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Sandbox: {Executable} exited {ExitCode} in {Elapsed:N0}ms TimedOut={TimedOut} (container, id={InvocationId})",
                argv[0], containerResult.ExitCode, containerResult.Elapsed.TotalMilliseconds,
                containerResult.TimedOut, outputHash);

            return new CommandResult(
                containerResult.ExitCode,
                containerResult.Stdout,
                containerResult.Stderr,
                containerResult.Elapsed,
                containerResult.TimedOut);
        }

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

    /// <summary>
    /// Rewrites a host working directory (under <paramref name="hostRoot"/>) to its
    /// container-side path (under <paramref name="mountPath"/>). The workspace root is
    /// bind-mounted at <paramref name="mountPath"/>, so a CWD of
    /// <c>{hostRoot}/item1/repo</c> maps to <c>{mountPath}/item1/repo</c>. If the CWD is
    /// not under the root, the mount path itself is used as a safe fallback.
    /// </summary>
    internal static string MapToContainerPath(string workingDirectory, string hostRoot, string mountPath)
    {
        var absoluteCwd = Path.GetFullPath(workingDirectory);
        var absoluteRoot = Path.GetFullPath(hostRoot);

        var relative = Path.GetRelativePath(absoluteRoot, absoluteCwd);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return mountPath;

        // Container paths are always POSIX-style.
        var posixRelative = relative.Replace('\\', '/');
        return $"{mountPath.TrimEnd('/')}/{posixRelative}";
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
    /// One segment of a compound command line together with the connector that
    /// precedes it. <see cref="Connector"/> is the empty string for the first
    /// segment, or one of <c>"&amp;&amp;"</c>, <c>"||"</c>, <c>";"</c>.
    /// </summary>
    internal readonly record struct CommandSegment(string Connector, string Text);

    /// <summary>
    /// Splits <paramref name="commandLine"/> into top-level segments on the shell
    /// connectors <c>&amp;&amp;</c>, <c>||</c> and <c>;</c>, honouring single and
    /// double quotes — a connector appearing inside quotes is NOT a separator. A
    /// lone <c>&amp;</c> or <c>|</c> is treated as an ordinary character (no shell
    /// background / pipe interpretation). Segment text is trimmed and keeps its
    /// original quoting so <see cref="Tokenise"/> can run on each segment.
    /// </summary>
    internal static IReadOnlyList<CommandSegment> SplitTopLevel(string commandLine)
    {
        var segments = new List<CommandSegment>();
        var current = new StringBuilder();
        var connector = string.Empty; // connector preceding the segment being built
        char? inQuote = null;

        void Flush(string nextConnector)
        {
            segments.Add(new CommandSegment(connector, current.ToString().Trim()));
            current.Clear();
            connector = nextConnector;
        }

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];

            if (inQuote.HasValue)
            {
                if (c == inQuote.Value) inQuote = null;
                current.Append(c);
                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    inQuote = c;
                    current.Append(c);
                    break;
                case ';':
                    Flush(";");
                    break;
                case '&' when i + 1 < commandLine.Length && commandLine[i + 1] == '&':
                    Flush("&&");
                    i++; // consume the second '&'
                    break;
                case '|' when i + 1 < commandLine.Length && commandLine[i + 1] == '|':
                    Flush("||");
                    i++; // consume the second '|'
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        segments.Add(new CommandSegment(connector, current.ToString().Trim()));
        return segments;
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
