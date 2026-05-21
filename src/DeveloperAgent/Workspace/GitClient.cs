using DeveloperAgent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeveloperAgent.Workspace;

/// <summary>
/// Shells out to the <c>git</c> CLI via <see cref="ICommandSandbox"/> to perform
/// the git operations needed by the lifecycle loop and the agent.
/// </summary>
public sealed class GitClient : IGitClient
{
    // Timeout for individual git commands.
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private readonly ICommandSandbox _sandbox;
    private readonly IOptions<WorkspaceOptions> _workspaceOptions;
    private readonly IOptions<GitHubOptions> _githubOptions;
    private readonly SecretsBundle _secrets;
    private readonly ILogger<GitClient> _logger;

    /// <summary>Initialises a new <see cref="GitClient"/>.</summary>
    public GitClient(
        ICommandSandbox sandbox,
        IOptions<WorkspaceOptions> workspaceOptions,
        IOptions<GitHubOptions> githubOptions,
        SecretsBundle secrets,
        ILogger<GitClient> logger)
    {
        _sandbox = sandbox;
        _workspaceOptions = workspaceOptions;
        _githubOptions = githubOptions;
        _secrets = secrets;
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
    public async Task PushAsync(TaskWorkspace ws, CancellationToken ct)
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

        var repoUrl = _githubOptions.Value.Repository.Url;
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

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the git command line for clone or push, prepending
    /// <c>-c http.extraheader="Authorization: Bearer {token}"</c> for HTTPS URLs.
    /// For file-path URLs (used in tests) the header is omitted.
    /// </summary>
    private string BuildGitCloneOrPushLine(string repoUrl, string gitVerb)
    {
        var isHttps = repoUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                   || repoUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

        if (isHttps && !string.IsNullOrEmpty(_secrets.GitHubToken))
        {
            var header = $"Authorization: Bearer {_secrets.GitHubToken}";
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
