using System.Text;
using Agent.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Workspace;

/// <summary>
/// Shells out to the <c>git</c> CLI via <see cref="ICommandSandbox"/> to perform
/// the git operations needed by the lifecycle loop and the agent.
/// </summary>
public sealed class GitClient : IGitClient
{
    // Timeout for individual git commands.
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private readonly ICommandSandbox _sandbox;
    private readonly DiffScopeLimitOptions _scopeLimits;
    private readonly IGitTokenProvider _tokenProvider;
    private readonly ILogger<GitClient> _logger;

    /// <summary>Initialises a new <see cref="GitClient"/>.</summary>
    public GitClient(
        ICommandSandbox sandbox,
        IOptions<DiffScopeLimitOptions> scopeLimits,
        IGitTokenProvider tokenProvider,
        ILogger<GitClient> logger)
    {
        _sandbox = sandbox;
        _scopeLimits = scopeLimits.Value;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task CloneAsync(TaskWorkspace ws, string repoUrl, CancellationToken ct)
    {
        // Clone into the parent of RepoRoot; git names the new dir "repo"
        var parentDir = Path.GetDirectoryName(ws.RepoRoot)
                        ?? throw new InvalidOperationException(
                            $"Cannot determine parent of RepoRoot '{ws.RepoRoot}'.");

        _logger.LogInformation("Cloning {Url} into {RepoRoot}", repoUrl, ws.RepoRoot);

        var commandLine = BuildGitCloneOrPushLine(repoUrl, $"clone \"{repoUrl}\" repo");
        var result = await _sandbox.RunAsync(commandLine, parentDir, DefaultTimeout, ct)
                                   .ConfigureAwait(false);
        EnsureSuccess(result, "git clone");
    }

    /// <inheritdoc />
    public async Task CheckoutNewBranchAsync(TaskWorkspace ws, CancellationToken ct)
    {
        var result = await _sandbox.RunAsync(
            $"git checkout -b {ws.BranchName}",
            ws.RepoRoot, DefaultTimeout, ct).ConfigureAwait(false);
        EnsureSuccess(result, $"git checkout -b {ws.BranchName}");
    }

    /// <inheritdoc />
    public async Task<string> ResolveDefaultBranchAsync(TaskWorkspace ws, CancellationToken ct)
    {
        // git symbolic-ref --short refs/remotes/origin/HEAD → e.g. "origin/main"
        var result = await _sandbox.RunAsync(
            "git symbolic-ref --short refs/remotes/origin/HEAD",
            ws.RepoRoot, DefaultTimeout, ct).ConfigureAwait(false);
        EnsureSuccess(result, "git symbolic-ref --short refs/remotes/origin/HEAD");

        // Output is "origin/main" — strip the remote prefix
        var output = result.Stdout.Trim();
        var slashIdx = output.IndexOf('/');
        return slashIdx >= 0 ? output[(slashIdx + 1)..] : output;
    }

    /// <inheritdoc />
    public async Task AddAsync(TaskWorkspace ws, IReadOnlyList<string> pathspecs, CancellationToken ct)
    {
        var args = string.Join(" ", pathspecs.Select(p => $"\"{p}\""));
        var result = await _sandbox.RunAsync(
            $"git add {args}",
            ws.RepoRoot, DefaultTimeout, ct).ConfigureAwait(false);
        EnsureSuccess(result, "git add");
    }

    /// <inheritdoc />
    public async Task CommitAsync(TaskWorkspace ws, string subject, string body, CancellationToken ct)
    {
        var message = string.IsNullOrWhiteSpace(body)
            ? subject
            : $"{subject}\n\n{body}";

        // Disable GPG signing: the agent never needs signed commits and gpg may not be
        // installed in the runtime environment.
        var result = await _sandbox.RunAsync(
            $"git -c commit.gpgsign=false commit -m \"{EscapeForQuotedArg(message)}\"",
            ws.RepoRoot, DefaultTimeout, ct).ConfigureAwait(false);
        EnsureSuccess(result, "git commit");
    }

    /// <inheritdoc />
    public async Task PushAsync(TaskWorkspace ws, string repoUrl, CancellationToken ct)
    {
        // Defence-in-depth: refuse to push the default branch
        var headResult = await _sandbox.RunAsync(
            "git symbolic-ref --short HEAD",
            ws.RepoRoot, DefaultTimeout, ct).ConfigureAwait(false);
        EnsureSuccess(headResult, "git symbolic-ref --short HEAD");

        var currentBranch = headResult.Stdout.Trim();
        if (string.Equals(currentBranch, ws.DefaultBranch, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Refusing to push: HEAD is on the default branch '{ws.DefaultBranch}'.");

        // Scope-limit gate (LLD §P2-H): refuse to push a diff that breaches the
        // configured changed-file / changed-line caps. Runs BEFORE the push command
        // so an oversized change never reaches the remote.
        var stats = await GetDiffStatsAsync(ws, ws.DefaultBranch, ct).ConfigureAwait(false);
        if (stats.ChangedFiles > _scopeLimits.MaxChangedFiles)
        {
            _logger.LogWarning(
                "Refusing to push: {Files} changed files exceeds MaxChangedFiles={Cap}",
                stats.ChangedFiles, _scopeLimits.MaxChangedFiles);
            throw new ScopeLimitExceededException(
                ScopeLimit.MaxChangedFiles, stats.ChangedFiles, _scopeLimits.MaxChangedFiles);
        }
        if (stats.ChangedLines > _scopeLimits.MaxChangedLines)
        {
            _logger.LogWarning(
                "Refusing to push: {Lines} changed lines exceeds MaxChangedLines={Cap}",
                stats.ChangedLines, _scopeLimits.MaxChangedLines);
            throw new ScopeLimitExceededException(
                ScopeLimit.MaxChangedLines, stats.ChangedLines, _scopeLimits.MaxChangedLines);
        }

        var commandLine = BuildGitCloneOrPushLine(
            repoUrl,
            $"push --set-upstream origin {ws.BranchName}");

        var result = await _sandbox.RunAsync(commandLine, ws.RepoRoot, DefaultTimeout, ct)
                                   .ConfigureAwait(false);
        EnsureSuccess(result, "git push");
    }

    /// <inheritdoc />
    public async Task<string> StatusAsync(TaskWorkspace ws, CancellationToken ct)
    {
        var result = await _sandbox.RunAsync(
            "git status --porcelain",
            ws.RepoRoot, DefaultTimeout, ct).ConfigureAwait(false);
        EnsureSuccess(result, "git status --porcelain");
        return result.Stdout;
    }

    /// <inheritdoc />
    public async Task<string> DiffAsync(TaskWorkspace ws, string @base, CancellationToken ct)
    {
        var result = await _sandbox.RunAsync(
            $"git diff {@base}...HEAD",
            ws.RepoRoot, DefaultTimeout, ct).ConfigureAwait(false);
        EnsureSuccess(result, $"git diff {@base}...HEAD");
        return result.Stdout;
    }

    /// <inheritdoc />
    public async Task<DiffStats> GetDiffStatsAsync(TaskWorkspace ws, string @base, CancellationToken ct)
    {
        var result = await _sandbox.RunAsync(
            $"git diff --numstat {@base}...HEAD",
            ws.RepoRoot, DefaultTimeout, ct).ConfigureAwait(false);
        EnsureSuccess(result, $"git diff --numstat {@base}...HEAD");
        return ParseNumstat(result.Stdout);
    }

    /// <summary>
    /// Parses <c>git diff --numstat</c> output into a <see cref="DiffStats"/>.
    /// Each non-empty line is <c>{added}\t{deleted}\t{path}</c>. Binary files emit
    /// <c>-\t-\t{path}</c> — they count as a changed file but contribute zero lines.
    /// </summary>
    internal static DiffStats ParseNumstat(string numstatOutput)
    {
        int files = 0;
        int lines = 0;

        foreach (var rawLine in numstatOutput.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ');
            if (line.Length == 0) continue;

            var parts = line.Split('\t');
            if (parts.Length < 3) continue; // malformed — skip defensively

            files++;

            // Binary files report "-" for added/deleted; treat as zero lines.
            if (int.TryParse(parts[0], out var added)) lines += added;
            if (int.TryParse(parts[1], out var deleted)) lines += deleted;
        }

        return new DiffStats(files, lines);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string BuildGitCloneOrPushLine(string repoUrl, string gitVerb)
        => BuildGitCommandLine(repoUrl, gitVerb, _tokenProvider.GetToken());

    /// <summary>
    /// Builds the git command line for clone or push. For HTTPS URLs with a token,
    /// prepends <c>-c http.extraheader="Authorization: Basic {base64(x-access-token:token)}"</c>.
    /// For file-path URLs (used in tests) the header is omitted.
    /// </summary>
    /// <remarks>
    /// GitHub's git-over-HTTPS smart transport authenticates with HTTP <b>Basic</b> auth —
    /// the token supplied as the password under the <c>x-access-token</c> username (the
    /// scheme <c>actions/checkout</c> uses). It does <b>not</b> accept <c>Bearer</c>, which
    /// is only valid for the REST / GraphQL API; sending <c>Bearer</c> makes GitHub answer
    /// <c>HTTP 401 — remote: invalid credentials</c> so the clone fails with exit 128.
    /// </remarks>
    internal static string BuildGitCommandLine(string repoUrl, string gitVerb, string? token)
    {
        var isHttps = repoUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                   || repoUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

        if (isHttps && !string.IsNullOrEmpty(token))
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));
            var header = $"Authorization: Basic {basic}";
            var escapedHeader = EscapeForQuotedArg(header);
            return $"git -c http.extraheader=\"{escapedHeader}\" {gitVerb}";
        }

        return $"git {gitVerb}";
    }

    private static void EnsureSuccess(CommandResult result, string operation)
    {
        if (result.TimedOut)
            throw new InvalidOperationException(
                $"git operation timed out: {operation}");
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"git operation failed (exit {result.ExitCode}): {operation}\n{result.Stderr}");
    }

    private static string EscapeForQuotedArg(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
