# ReviewerAgent — Design Spec

**Date:** 2026-06-08
**Status:** Approved (design); pending implementation plan
**Author:** brainstormed with Claude Code

## Goal

Stand up an **independent, standalone reviewer agent service** — `ReviewerAgent` — built with the same
technology stack as `DeveloperAgent` (.NET 10 / Aspire, Microsoft Agent Framework + Anthropic provider,
`Agent.GitHub` for deterministic GitHub access). It polls a configured GitHub repository for open pull
requests, analyses each PR's diff, and either **approves** the PR or **requests changes** with review
comments. All configuration is done in an `appsettings.json` file, the same way as `DeveloperAgent`.

The service is the *single* reviewer for the repo: `DeveloperAgent` stops reviewing its own PRs and only
waits for `ReviewerAgent`'s verdict.

## Context: what already exists

A reviewer agent already lives **inside** `DeveloperAgent`:

- `DeveloperAgent/Agent/Review/IReviewerAgent.cs`, `ReviewerAgent.cs`, `SubmitReviewTool.cs`
- `ReviewResult` record (`Verdict`, `Summary`, `UsedModel`)
- `ReviewerOptions` (`PersonaPath`, `MaxDiffFiles`, `MaxDiffLines`), `ReviewerPersonaLoader`
- `personas/reviewer.md` (the reviewer system prompt)

`ReviewerAgent.ReviewAsync(prNumber, ct)` today:
1. `GetPullRequestForReviewAsync` → `PullRequestReviewContext` (body, file/line counts, unified diff).
2. **Deterministic check 1** — four-section PR body present? (reads `PullRequestBodyBuilder.RequiredSectionHeaders`).
   Missing → `RequestChanges` (no model used).
3. **Deterministic check 2** — diff over `MaxDiffFiles`/`MaxDiffLines`? → `RequestChanges` (no model used).
4. **Model-backed persona scan** — `ChatClientAgent` + reviewer persona + `SubmitReviewTool`; model must call
   `submit_review` once with `{ verdict, summary }`. No call / error → fail-closed to `RequestChanges`.
5. `SubmitReviewAsync(prNumber, verdict, summary, ct)`. **Never merges.**

`Agent.GitHub` already exposes the PR-review surface on `IGitHubProjectsClient`:
`GetPullRequestForReviewAsync`, `SubmitReviewAsync`, `GetPullRequestStatusAsync`,
`GetReviewFeedbackSinceAsync`; models `PullRequestReviewContext`, `PullRequestStatus`, `PullRequest`,
`ReviewVerdict` (Approve / RequestChanges / Comment).

So this work is mostly **extraction + a new thin host + a poll/trigger loop**, not a from-scratch build.

## Decisions (locked during brainstorming)

| Decision | Choice |
|---|---|
| Code reuse | **Extract** the review mechanics into a new `src/AgenticTools/Agent.Review/` library, consumed by the new host. |
| Trigger | **Poll on an interval** (a hosted lifecycle loop, like `AgentLifecycleService`). |
| Ownership | **Take over** — `ReviewerAgent` is the single reviewer; `DeveloperAgent` stops invoking its own. |
| Poll source | **Open PRs in the repo** (board-independent; adds a list-open-PRs method to `Agent.GitHub`). |
| Idempotency | **GitHub as source of truth** — no Dapr/Redis state store; the service is stateless. |
| Service name | **`ReviewerAgent`** (host project, mirroring `DeveloperAgent`). |

## Architecture

Two new pieces, following the repo's **generic-mechanics (AgenticTools) vs. host-policy** split.

### Agent.Review (new library, agent-neutral)

`src/AgenticTools/Agent.Review/` — the review *mechanics*, extracted from `DeveloperAgent`:

- `IReviewerAgent` / `ReviewerAgent` (engine), `SubmitReviewTool`, `ReviewResult`, deterministic checks,
  `ReviewerOptions`, generic persona loading, and an `AddReviewServices(...)` DI entry point.
- Depends **only** on `Agent.GitHub` + `Agent.Runtime`. Never references a host.
- Dependency edges: `Agent.GitHub` + `Agent.Runtime` ← `Agent.Review` ← `ReviewerAgent` (host).

Two decoupling changes during extraction so the library stays policy-free:

1. Depend on the agent-neutral **`IGitHubProjectsClient`** (which already carries the PR-review methods),
   not the `DeveloperAgent` facade `IGitHubProjectService`.
2. The four-section PR-body check currently reads `PullRequestBodyBuilder.RequiredSectionHeaders`
   (a `DeveloperAgent` §9 policy constant). Replace with a configurable
   **`ReviewerOptions.RequiredPrBodySections`** (list of header strings). When the list is empty, the
   section check is skipped. The host supplies the four §9 headers via `appsettings.json`.

Behavior is otherwise unchanged from the existing `ReviewerAgent` (deterministic checks first, then the
model-backed persona scan, fail-closed to `RequestChanges`, never merges).

**Naming note:** the engine class is `Agent.Review.ReviewerAgent`; the new host project/assembly is also
named `ReviewerAgent` (root namespace `ReviewerAgent`). They stay distinct by namespace — reference the
engine through its `Agent.Review` namespace. No collision.

### ReviewerAgent (new host service)

`src/ReviewerAgent/` — a thin host (sibling of `DeveloperAgent`) that wires the libraries and owns reviewer
*policy*. **No Dapr, no actors, no workflow, no sandbox/workspace, no dashboard** — it never runs code, only
reads diffs and posts reviews.

Wiring (`Program.cs`): `AddServiceDefaults`, `AddAgentRuntimeServices`, `AddGitHubProjectServices(...)`
(with the egress + resilience callback, as `DeveloperAgent` does), `AddReviewServices`. Two secret seams —
`IAnthropicApiKeyProvider` and `IGitHubTokenProvider` — wired from the same env-var/user-secrets resolution
pattern `DeveloperAgent` uses. `personas/reviewer.md` is shared/copied into the project.

## New Agent.GitHub mechanics

Two additions to `IGitHubProjectsClient` (repo-centric, board-independent), implemented over the existing
REST transport (Octokit types stay inside the transport):

- `Task<IReadOnlyList<PullRequest>> ListOpenPullRequestsAsync(CancellationToken ct)` — open PRs with
  `Number` + `HeadSha` (+ a draft flag; `PullRequest` gains an `IsDraft` field or a sibling record as needed).
- `Task<IReadOnlyList<string>> GetReviewedHeadShasAsync(int prNumber, string reviewerLogin, CancellationToken ct)`
  — the head SHAs already reviewed by the bot account (for idempotency).

## Polling lifecycle + idempotency

`ReviewLifecycleService` (`IHostedService` / `BackgroundService`, mirroring `AgentLifecycleService`):

1. Every `Agent.PollIntervalSeconds`, call `ListOpenPullRequestsAsync`.
2. Skip drafts (when `Reviewer.SkipDrafts`) and PRs failing the optional `Reviewer.AuthorAllowList`.
3. **Idempotency via GitHub (no state store):** skip if the PR's current `HeadSha` is already in
   `GetReviewedHeadShasAsync(prNumber, Reviewer.ReviewerLogin, ct)`. The service is fully stateless and
   crash-safe — GitHub is the record of what was reviewed. Pushing new commits changes the head SHA, so a
   stale review no longer matches and the PR is re-reviewed automatically.
4. Otherwise call `IReviewerAgent.ReviewAsync(prNumber, ct)`, which reviews and posts the verdict.

Optional convenience endpoint: `POST /review/{prNumber}` for on-demand manual review (same code path).
Health/info: `AddServiceDefaults` + `MapDefaultEndpoints` (`/livez`, `/uptime`, `/info`, `/health`,
`/alive`). Kestrel on `http://*:8090` (`DeveloperAgent` uses 8089).

## Configuration (ReviewerAgent/appsettings.json)

Trimmed vs. `DeveloperAgent` — only what a reviewer needs:

```jsonc
"Agent":   { "Name": "ReviewerAgent", "Model": "claude-opus-4-7", "Effort": "xhigh",
             "PersonaPath": "personas/reviewer.md", "PollIntervalSeconds": 60 },
"Reviewer":{ "MaxDiffFiles": 50, "MaxDiffLines": 2000,
             "RequiredPrBodySections": ["## Summary","## User-visible behavior",
                                        "## Tests/validation run","## Notes/assumptions"],
             "ReviewerLogin": "<bot-account>", "SkipDrafts": true, "AuthorAllowList": [] },
"Anthropic": { "ApiKeySecretName": "anthropic-api-key" },
"GitHub":  { "Owner": "mchudinov",
             "Repository": { "Name": "TicTacToe2", "Url": "https://github.com/mchudinov/TicTacToe2",
                             "DefaultBranch": "main" },
             "TokenSecretName": "github-token" },
"Egress":  { "AllowedHosts": ["api.anthropic.com","api.github.com","*.githubusercontent.com"] },
"HttpResilience": { "AttemptTimeoutSeconds": 60 },
"Kestrel": { "EndPoints": { "Http": { "Url": "http://*:8090" } } }
```

No `Project`, `Workspace`, `Sandbox` command lists, `ContainerRuntime`, `ScopeLimits`, or Dapr config.
(`AddGitHubProjectServices` may today expect `ProjectOptions`; if so, make it optional/unused for this host,
since the poll source is open PRs, not a board.) Per the CLAUDE.md array-binding gotcha, list defaults
(`AllowedHosts`, `RequiredPrBodySections`, `AuthorAllowList`) live **only** in `appsettings.json`, not seeded
in the records.

## DeveloperAgent "takes over" change

`DeveloperAgent` stops being a reviewer:

- Remove its reviewer **invocation** — locate the current call site of `IReviewerAgent.ReviewAsync`
  (an activity or the lifecycle; **to be confirmed during planning before deleting**).
- Drop the `IReviewerAgent` / `ReviewerPersonaLoader` DI registrations and the `Reviewer` config section.
- Remove its dependency on the review code (now in `Agent.Review`, no longer referenced by `DeveloperAgent`).
- `WaitForReviewActivity` **stays** — it already only polls PR status and raises `ChangesRequested` / `Merged`
  external events, so it now waits for the *external* `ReviewerAgent`'s verdict.

## AppHost + tests

- **AppHost:** `builder.AddProject<Projects.ReviewerAgent>("ReviewerAgent")` — **no Dapr sidecar** (stateless).
  Optional egress reference.
- **Tests (TDD per CLAUDE.md):**
  - new `src/Tests/Agent.Review.Tests/` — move + adapt the existing `ReviewerAgentTests` (deterministic checks,
    persona scan, configurable required sections).
  - new `src/Tests/ReviewerAgent.Tests/` — lifecycle polling, idempotency-skip, draft/author filtering, the
    manual endpoint.
  - additions to `Agent.GitHub.Tests` — `ListOpenPullRequestsAsync` and `GetReviewedHeadShasAsync`.

## Assumptions

- Reviewer **never merges**; a human or `DeveloperAgent` merges after approval.
- Reviews **all** open non-draft PRs by default; author filtering is opt-in config.
- Idempotency keyed on **(PR, head SHA, bot login)** via GitHub; re-reviews when new commits are pushed.
- **No operator dashboard** in v1 (YAGNI) — health/info endpoints only.

## Out of scope (v1)

GitHub webhooks (poll only), operator dashboard, merging, multi-repo, and any non-board repo configuration
beyond the single configured repository.
