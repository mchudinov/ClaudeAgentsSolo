# 02 — GitHub via Octokit

**Status:** draft — 2026-05-20
**Depends on:** `01-configuration-and-process-shape.md` (needs `GitHubOptions` + `SecretsBundle`).
**Unblocks:** `05-lifecycle-loop.md`.

> **Update (Step-37):** the deterministic GitHub access described here has been extracted into the
> standalone, agent-neutral **`ClaudeAgents.GitHub`** library. The generic, status-name-keyed client
> is now `IGitHubProjectsClient` (in `src/ClaudeAgents.GitHub/`); the typed `IGitHubProjectService`
> described below survives as a thin developer-agent **facade** (`src/DeveloperAgent/GitHub/`) that
> maps `ProjectState` ↔ column names and delegates to the client. The library authenticates via an
> `IGitHubTokenProvider` seam (not `SecretsBundle` directly) and is wired by `AddGitHubProjectServices`,
> with the host composing egress + resilience. See CLAUDE.md › "GitHub access layer" for the current shape.

## Purpose

All deterministic GitHub interactions go through one service. The lifecycle loop never touches Octokit directly — it asks `IGitHubProjectService` for the next Ready item, asks it to transition states, asks it to open a PR. Centralising this is what makes it possible to mock GitHub in tests and what makes the future MCP-based exploration path additive instead of redundant.

The LLD names **Octokit.GraphQL.NET** for deterministic operations. GitHub Projects v2 is GraphQL-only, so that part is non-negotiable; PR creation and review-state polling are REST and use the standard **Octokit.NET** client.

## Deliverables

| Path | Change |
| ---- | ------ |
| `src/DeveloperAgent/DeveloperAgent.csproj` | Add `PackageReference` for `Octokit` (REST) and `Octokit.GraphQL` (GraphQL). |
| `src/DeveloperAgent/GitHub/IGitHubProjectService.cs` | Public interface (see §Public surface). |
| `src/DeveloperAgent/GitHub/GitHubProjectService.cs` | Implementation. Wraps both Octokit clients. Constructed with `GitHubOptions` + `SecretsBundle`. |
| `src/DeveloperAgent/GitHub/PullRequestBodyBuilder.cs` | Builds the four-section PR body from `personas/developer.md` §9. Used by the agent core, not by the lifecycle loop directly. |
| `src/DeveloperAgent/GitHub/GitHubModels.cs` | DTO records returned across the boundary — no Octokit types leak past this layer. |
| `src/DeveloperAgent/Program.cs` | Add: `builder.Services.AddSingleton<IGitHubProjectService, GitHubProjectService>();` |

The DI registration is a singleton — the Octokit clients are thread-safe and the secret bundle is immutable for the process lifetime.

## Public surface

```csharp
namespace DeveloperAgent.GitHub;

public interface IGitHubProjectService
{
    // Returns the next Ready item, or null if none. Selection order: by position in the Ready column (top first).
    Task<ProjectItem?> TryGetNextReadyItemAsync(CancellationToken ct);

    Task MoveItemAsync(string projectItemId, ProjectState target, CancellationToken ct);

    Task AddItemCommentAsync(string projectItemId, string markdownBody, CancellationToken ct);

    // PR operations target the configured repository (GitHubOptions.Repository).
    Task<PullRequest> CreatePullRequestAsync(CreatePullRequest request, CancellationToken ct);

    Task<PullRequestStatus> GetPullRequestStatusAsync(int pullRequestNumber, CancellationToken ct);

    // Fetches review-thread comments and issue comments newer than `sinceUtc` on the PR,
    // concatenated as a single markdown blob ready to feed back to the agent as PriorReviewFeedback.
    // Used by the lifecycle loop on a ChangesRequested verdict (plan 05 §REVIEW WAIT LOOP).
    Task<string> GetReviewFeedbackSinceAsync(int pullRequestNumber, DateTimeOffset sinceUtc, CancellationToken ct);

    // Returns the items currently sitting in In Progress / In Review on startup, so the loop can log and skip them.
    Task<IReadOnlyList<ProjectItem>> GetInFlightItemsAsync(CancellationToken ct);
}

public enum ProjectState { Ready, InProgress, InReview, Done }

public sealed record ProjectItem(
    string ProjectItemId,         // GraphQL node ID of the ProjectV2Item
    string ContentNodeId,         // Issue or PR node ID — used for comments
    int    ContentNumber,         // issue / PR number
    string Title,
    string BodyMarkdown,
    ProjectState State);

public sealed record CreatePullRequest(
    string HeadBranch,
    string BaseBranch,
    string Title,                 // ≤ 72 chars per persona §9
    string MarkdownBody);         // already four-section-formatted by PullRequestBodyBuilder

public sealed record PullRequest(
    int Number,
    string HeadSha,
    string HtmlUrl);

public sealed record PullRequestStatus(
    int Number,
    PullRequestReviewState Review,
    bool ChecksGreen,
    bool Merged,
    string HeadSha);

public enum PullRequestReviewState { Pending, ChangesRequested, Approved }
```

```csharp
namespace DeveloperAgent.GitHub;

public static class PullRequestBodyBuilder
{
    public static string Build(
        string summary,
        string userVisibleBehavior,         // or "No user-visible behavior change"
        string testsValidationRun,          // include "Tests not run" subsection if any
        string notesAssumptions);           // or "None"
}
```

The builder enforces the four headings in order, fills `"None"`/`"N/A"` where the caller passes empty, and refuses empty `summary` (throws — a malformed PR body is a `ChangesRequested` per the persona, and we should not let the agent post one).

## Behavior

### State mapping

`ProjectState` names come from `GitHubOptions.States` — the strings on the GitHub side are configurable (`Ready` / `In Progress` / `In Review` / `Done` are defaults, the underlying field is a `ProjectV2SingleSelectField` on the project). The service caches the field's option IDs at startup so transitions are O(1) GraphQL mutations.

### `TryGetNextReadyItemAsync`

Query the project (`organization(login: ...) { projectV2(number: ...) }` for `OwnerType=Organization`; `user(login: ...)` otherwise) for items where the status field equals the configured `Ready` option ID. Order by item position. Return the first item. Materialize the issue body + title via the `content` union (`Issue` / `PullRequest` / `DraftIssue`); reject `DraftIssue` content (the agent cannot operate on it) and skip those items.

### `MoveItemAsync`

Single GraphQL mutation `updateProjectV2ItemFieldValue` with the cached option ID. Idempotent — calling it with the current state is a no-op. The service logs the transition.

### `AddItemCommentAsync`

GraphQL `addComment` on the underlying issue node (`content.id`). The first comment posted per task is the implementation plan (see plan 04 §Planning phase). Subsequent comments are status updates ("build green", "tests passing", "PR opened: #N", "ChangesRequested received, iterating"). The format is plain markdown; the service does not mutate the body.

### `CreatePullRequestAsync`

REST: `POST /repos/{owner}/{repo}/pulls`. Title and body come from the caller (the agent core, using `PullRequestBodyBuilder`). The base branch is `GitHubOptions.Repository.DefaultBranch`. The head branch is whatever the agent pushed. The service returns the PR number + SHA + URL.

If the call fails because a PR already exists for that head (the agent ran a second round after a `ChangesRequested`), the service catches the specific `422 unprocessable_entity` Octokit response, queries the existing PR for that head, and returns it. **One PR per task** is the persona rule (§8); the service enforces it by behaviour, not by an extra check.

### `GetPullRequestStatusAsync`

REST. Combine three calls:

1. `GET /repos/{owner}/{repo}/pulls/{number}` — `merged`, `head.sha`, `mergeable_state`.
2. `GET /repos/{owner}/{repo}/pulls/{number}/reviews` — most recent non-dismissed review per reviewer; collapse to `Approved` (latest is APPROVED), `ChangesRequested` (latest is CHANGES_REQUESTED), or `Pending`.
3. `GET /repos/{owner}/{repo}/commits/{head.sha}/check-runs` — `ChecksGreen` = every check `conclusion` ∈ {`success`, `neutral`, `skipped`}. If any check is still `in_progress` or `queued`, `ChecksGreen` is `false` (not green yet, not red yet — the loop polls again).

The three calls are batched with `Task.WhenAll`. The service does **not** decide whether to move to Done — that's the lifecycle loop's job. It only returns the state.

### `GetReviewFeedbackSinceAsync`

REST. Two calls in parallel:

1. `GET /repos/{owner}/{repo}/pulls/{number}/comments` — review-thread comments (line-attached). Filter `created_at > sinceUtc`.
2. `GET /repos/{owner}/{repo}/issues/{number}/comments` — issue-level comments on the PR. Filter `created_at > sinceUtc`.

Concatenate into one markdown blob: each entry rendered as

```text
> **@{user.login}** on {created_at}{ — file {path}:{line} if line-attached}:
> {body, every line prefixed with '> '}
```

Order chronologically by `created_at`. Return empty string if nothing newer than `sinceUtc`. The caller (`TaskExecutor`) is responsible for advancing the `sinceUtc` cursor (see plan 05 §`TaskState`).

The service does **not** filter by reviewer identity, dismiss state, or PR thread resolution status in phase 1 — every new comment goes to the agent and the persona decides what to address. Phase 2 may add filtering once the reviewer agent is wired up.

### `GetInFlightItemsAsync`

Same query as `TryGetNextReadyItemAsync` but matches the `InProgress` and `InReview` option IDs. Used once at startup so the lifecycle loop can log them and skip per the phase-1 "no recovery" rule (see `00-roadmap.md` §What is OUT). Phase 2 wires recovery against the durable actor state instead.

## Authentication

Both clients are constructed with the resolved `SecretsBundle.GitHubToken`:

- `Octokit.GitHubClient` — `new ProductHeaderValue("DeveloperAgent", versionFromAssembly)` + `Credentials(token)`.
- `Octokit.GraphQL.Connection` — `new ProductHeaderValue(...)` + same token.

The token is read once at startup and held for the process lifetime. Rotation is a phase-2 concern.

## Out of scope (deferred to phase 2)

- **GitHub MCP** — the lifecycle loop never calls MCP in phase 1. The agent's "explore the repo" step in phase 1 uses local filesystem reads inside the cloned workspace (see plan 04 §Tool surface).
- **Webhooks** — approval polling is by REST, every `ReviewPollIntervalSeconds`. No public webhook endpoint.
- **Multi-repo support** — exactly one configured repository per agent process.
- **Branch protection inspection / waiting on required checks** — phase 1's `ChecksGreen` is a boolean on the head SHA; phase 2 checks the protection rule set explicitly.
- **Tightening the `Done` gate** — phase 1 moves an item to `Done` when `Merged && ChecksGreen && Review == Approved`. The LLD also requires "branch protection requirements are satisfied" (idea.md §14); that exact check is phase 2.
- **GitHub App installation token (instead of PAT)** → phase 2.

## Verification

- A unit test against an `IGitHubProjectService` fake exercises every transition (`Ready → InProgress → InReview → Done`).
- `PullRequestBodyBuilder` unit tests cover: all four headings present in order, empty optional fields become `"None"`, empty `summary` throws.
- An integration test (opt-in via env flag `GITHUB_INTEGRATION_REPO`) hits a real test repo + project, creates a draft issue in `Ready`, runs the full transition cycle, and cleans up.
