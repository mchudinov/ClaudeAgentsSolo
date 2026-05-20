# 03 — Workspace, Git, and Sandbox

**Status:** draft — 2026-05-20
**Depends on:** `01-configuration-and-process-shape.md` (needs `WorkspaceOptions` + `SecretsBundle`).
**Unblocks:** `04-anthropic-agent-core.md` (the agent's `shell_run` and `git_*` tools are exposed by this layer), `05-lifecycle-loop.md`.

## Purpose

Owns everything that touches the local filesystem and child processes: the workspace directory, the `git` CLI, the build/test commands, and the allowlist that gates them. The agent and the lifecycle loop never call `Process.Start` directly — they go through this layer so the allowlist is enforced in one place.

In phase 1 the sandbox is **allowlist-only**: a command runs if and only if it matches an entry in `WorkspaceOptions.AllowedCommands` and its working directory is inside the workspace root. Deny rules (`~/.ssh`, `.env`, force push, branch-protection changes) and secret-file blocklists are phase 2.

## Deliverables

| Path | Change |
| ---- | ------ |
| `src/DeveloperAgent/Workspace/IWorkspaceManager.cs` | Public interface: prepare/clean per-task workspace. |
| `src/DeveloperAgent/Workspace/WorkspaceManager.cs` | Creates the per-item workspace under `WorkspaceOptions.RootPath`, clones the configured repo, returns a `TaskWorkspace` handle. |
| `src/DeveloperAgent/Workspace/IGitClient.cs` | Public interface for git operations needed by the loop and the agent. |
| `src/DeveloperAgent/Workspace/GitClient.cs` | Implementation that shells out to `git` via the sandbox runner. |
| `src/DeveloperAgent/Workspace/ICommandSandbox.cs` | Public interface for running an external command under the allowlist. |
| `src/DeveloperAgent/Workspace/CommandSandbox.cs` | Implementation. Validates allowlist, validates cwd, captures stdout/stderr, enforces a per-call timeout, returns a typed result. |
| `src/DeveloperAgent/Workspace/BranchName.cs` | Static helper that builds the agent branch name per persona §7. |
| `src/DeveloperAgent/Workspace/WorkspaceModels.cs` | DTO records (`TaskWorkspace`, `CommandResult`). |
| `src/DeveloperAgent/Program.cs` | DI: `AddSingleton<ICommandSandbox, CommandSandbox>()`, `AddSingleton<IGitClient, GitClient>()`, `AddSingleton<IWorkspaceManager, WorkspaceManager>()`. |

## Public surface

```csharp
namespace DeveloperAgent.Workspace;

public interface IWorkspaceManager
{
    Task<TaskWorkspace> PrepareAsync(string projectItemId, string branchName, CancellationToken ct);
    Task ReleaseAsync(TaskWorkspace workspace, CancellationToken ct);   // wipe the dir on success or terminal failure
}

public sealed record TaskWorkspace(
    string ProjectItemId,
    string BranchName,
    string RepoRoot,             // absolute, e.g. /workspace/{itemId}/repo
    string DefaultBranch);       // captured from origin/HEAD at clone time

public interface IGitClient
{
    Task CloneAsync(TaskWorkspace ws, string repoUrl, CancellationToken ct);
    Task CheckoutNewBranchAsync(TaskWorkspace ws, CancellationToken ct);   // checkout -b ws.BranchName, fails if branch exists locally
    Task<string> ResolveDefaultBranchAsync(TaskWorkspace ws, CancellationToken ct);  // from refs/remotes/origin/HEAD
    Task AddAsync(TaskWorkspace ws, IReadOnlyList<string> pathspecs, CancellationToken ct);
    Task CommitAsync(TaskWorkspace ws, string subject, string body, CancellationToken ct);
    Task PushAsync(TaskWorkspace ws, CancellationToken ct);                // pushes ws.BranchName; refuses if HEAD is on default branch
    Task<string> StatusAsync(TaskWorkspace ws, CancellationToken ct);
    Task<string> DiffAsync(TaskWorkspace ws, string @base, CancellationToken ct);  // for the PR-body builder
}

public interface ICommandSandbox
{
    Task<CommandResult> RunAsync(
        string commandLine,                  // e.g. "dotnet build src/Foo.csproj --no-restore"
        string workingDirectory,             // must be inside Workspace.RootPath
        TimeSpan timeout,
        CancellationToken ct);
}

public sealed record CommandResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Elapsed,
    bool TimedOut);

public static class BranchName
{
    public static string ForTask(string? threadId, string taskTitle);
    // - If threadId is non-empty, returns $"agent/{threadId[..8]}".
    // - Else: $"agent/{slug(taskTitle)}-{shortHash(taskTitle)}",
    //   slug = lowercase ASCII, hyphens for spaces, no other punctuation,
    //   total length ≤ 40 chars, trim trailing hyphens.
}
```

## Behavior

### Workspace layout

```text
{WorkspaceOptions.RootPath}/
  {projectItemId}/
    repo/                  # full clone of the configured repository
    logs/
      build.log
      test.log
      session.json         # in-memory agent session, persisted only on graceful shutdown — phase-1 nice-to-have
```

`PrepareAsync`:
1. Compute `dir = {RootPath}/{projectItemId}`. If it exists, wipe it (`Directory.Delete(recursive: true)`) — a leftover dir from a prior crash is fine to discard in phase 1 (no recovery).
2. Create `dir` and `dir/repo` and `dir/logs`.
3. Clone the configured repository via `IGitClient.CloneAsync(ws, GitHubOptions.Repository.Url, ct)`.
4. Resolve the default branch from `refs/remotes/origin/HEAD` and store it on the returned `TaskWorkspace`.

`ReleaseAsync` wipes `dir` after success or terminal failure. On non-terminal failure (transient build error mid-iteration) the workspace is kept so the next attempt is faster — but in phase 1 there is no "next attempt across restarts", so this is purely intra-process retry support.

### Git authentication

`GitClient` sets up token-bearing remote access by configuring an `http.extraheader` for the clone:

```text
git -c http.extraheader="Authorization: Bearer <token>" clone <repoUrl> repo
```

The token comes from `SecretsBundle.GitHubToken`. It is **never** written to the workspace (no `~/.git-credentials`, no inline URL `https://x-access-token:{token}@…`), so a worker compromise or a stray `git config --list` does not leak it.

**Verify before pinning:** Git's `http.extraheader` accepts both `Authorization: Bearer <token>` (RFC-compliant Bearer scheme) and `AUTHORIZATION: token <token>` (GitHub's legacy `token` scheme). Confirm the current GitHub Apps / fine-grained-PAT requirement via Context7 (CLAUDE.md already mandates Context7 for library/API lookups) before settling on the exact header form, and ensure subsequent `git push` reuses the credential (either via `http.extraheader` set in `git config` inside the clone, or by passing `-c http.extraheader=...` on every invocation through the sandbox).

### Branch creation and push protection

`CheckoutNewBranchAsync` runs `git checkout -b {ws.BranchName}` **before any edit** (matches persona §6). If a local branch with that name already exists (e.g. residual from a failed cleanup), the call fails and the loop surfaces the error — no implicit branch reuse in phase 1.

`PushAsync` runs `git symbolic-ref --short HEAD` first and refuses to proceed if HEAD is `ws.DefaultBranch`. This is a defence-in-depth check on top of the agent never being instructed to push the default branch — it costs nothing and protects against agent mistakes.

`PushAsync` uses `git push --set-upstream origin {ws.BranchName}` on the first round, `git push` on subsequent rounds. `--force` is not in the allowlist — `WorkspaceOptions.AllowedCommands` contains only `git push`, and the sandbox rejects `git push --force`. The persona §1 also forbids force-pushing the agent branch unless explicitly necessary — which it never is in phase 1.

### Allowlist enforcement

`CommandSandbox.RunAsync(commandLine, cwd, …)`:

1. **CWD check** — `Path.GetFullPath(cwd)` must start with `Path.GetFullPath(WorkspaceOptions.RootPath)` + path separator. Otherwise throw `SandboxViolationException("cwd outside workspace root")`. This blocks the agent from running a command in `/etc` or `~/.ssh`, even if the command itself were allowed.
2. **Prefix match** — tokenise `commandLine` into argv (a simple split honouring single/double quotes is enough; the agent's tool surface in plan 04 only accepts argv arrays, never raw shells, so tokenising at this layer is just for `dotnet`/`git` invocations from `GitClient`). Then check whether any entry in `WorkspaceOptions.AllowedCommands` is a prefix of the argv: an allowed `"dotnet build"` matches `["dotnet","build","src/Foo.csproj","--no-restore"]`. Match is on **whole tokens** — `"git push"` does not match `["git","pushplus"]`.

   **Allowlist contents (phase 1 defaults — see plan 01):** the orchestrator's own ops `git clone` and `git symbolic-ref` are in the default list alongside the agent-issued ops. Without them, `IWorkspaceManager.PrepareAsync` and `GitClient.PushAsync` cannot start. Both are sanctioned orchestrator usage of the same sandbox so the allowlist remains the single source of truth in phase 1; phase 2 splits orchestrator-trusted ops out via a separate interface.

   **`-c key=value` prefix flags on `git`:** the auth header is passed as `git -c http.extraheader="…" clone …`. The allowlist match must skip leading `-c <key>=<value>` token pairs when computing the matched prefix, otherwise `["git","-c","http.extraheader=…","clone",…]` does not match the `"git clone"` allowlist entry. Implement as: strip leading `-c <kv>` pairs from argv before prefix-matching. No other top-level git options are honoured in phase 1.
3. **No shell interpretation** — `Process.Start` with `UseShellExecute=false`, `argv` populated directly. No `&&`, `||`, redirection, globbing, or environment substitution. If the agent wants to run two commands, it calls the tool twice.
4. **Captures + timeout** — stdout and stderr captured in full (bounded by `MaxCaptureBytes`, default 1 MiB each — truncate with a clear marker). On timeout, kill the process tree and return `TimedOut = true` with whatever was captured.
5. **Logging** — every invocation logged at `Information` with command line, exit code, elapsed, and a hash of stdout/stderr (not the full bodies; full bodies go to `logs/<command>.log` inside the workspace). Secrets in env vars are not logged.

`SandboxViolationException` is fatal to the current task: the lifecycle loop catches it, comments the violation on the project item (without the offending command line — that's logged but not posted), and moves on to the next item. It is **never** retried with a different command — the agent already chose a forbidden path, and phase 1 does not try to coach it.

## Out of scope (deferred to phase 2)

- **Deny rules** — denied paths (`~/.ssh`, `.env`, `.git/config` writes, anywhere outside the workspace), denied commands (`curl`, `wget`, `chmod +x`, `git push --force`, anything writing GitHub CI secrets or branch protection), denied env-var reads.
- **`MaxChangedFiles` / `MaxChangedLines` enforcement** — phase 2 inspects `git diff --numstat` against the configured ceilings before allowing a push.
- **Container isolation per task** — phase 1 runs everything in the agent process's user. Phase 2 may run commands in an isolated container/Firecracker/runc child.
- **Concurrent tasks** — phase 1 processes one item at a time. Per-item subdirectories are already namespaced so phase 2's concurrent execution does not collide.
- **`git rebase`, `git merge`, `git reset --hard`** — not in the allowlist; the agent does not need them in phase 1.

## Verification

- Unit tests on `BranchName.ForTask` cover: thread-id path, slug path, length ≤ 40, lowercase ASCII only, slug with punctuation gets sanitised, deterministic hash.
- Unit tests on `CommandSandbox` (with a fake `IProcessRunner` interface internal to the class) cover: allowlist hit, allowlist miss, cwd-escape rejection, timeout path, exit-code propagation.
- An integration test (opt-in via `GIT_INTEGRATION_REPO`) clones a public sandbox repo into a temp dir, creates a branch, commits, refuses to push (no remote token) but verifies the push command would have been allowed. The default branch + branch name + workspace layout are asserted on disk.
