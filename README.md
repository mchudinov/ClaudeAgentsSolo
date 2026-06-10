# Developer agents cluster

## Developer and Reviewer workflow

The cluster runs **two independent services**, both started by the Aspire AppHost
(`src/AppHost/AppHost.cs`): the **DeveloperAgent** (a Blazor Server host that owns a Dapr
Workflow + Actors per item) and the **ReviewerAgent** (a stateless web service that polls open
PRs). They **never call each other directly**. Their only shared medium is **GitHub itself** —
the project-board column and the pull-request state (review verdict + head SHA + merged flag).
The collaboration is therefore *polling-based and eventually consistent*: each side watches
GitHub on its own interval and reacts to what it finds.

| | DeveloperAgent | ReviewerAgent |
| --- | --- | --- |
| Role | Picks up `Ready` board items, implements them, opens PRs, drives the item to `Done` | Reviews every open PR and posts a single verdict |
| Engine | Dapr Workflow (`DeveloperTaskWorkflow`, one instance per item) + `ProgrammingTaskActor` | `ReviewLifecycleService` background poller → `ReviewerAgent.ReviewAsync` |
| What it watches | Its PR's review state, on a 1-minute cadence inside the workflow's review loop | The repo's open PRs, on `Agent:PollIntervalSeconds` |
| Verdict surface | n/a | Binary gate: **Approve** or **RequestChanges** (posted via the internal `submit_review` tool → Octokit) |
| Merges? | **No** — it only *waits to observe* a merged+approved PR | **No** — merging is an out-of-band (human) action; the reviewer is explicitly forbidden to merge |

### The handshake

```text
DeveloperAgent                          GitHub (shared state)                 ReviewerAgent
──────────────                          ─────────────────────                 ─────────────
poll board → Ready item
start DeveloperTaskWorkflow
  Acquire → Branch → Plan (agent codes)
  └─ opens PR ───────────────────────►  PR opened (head SHA #1)
  CreatePullRequestActivity
  └─ move item ──────────────────────►  board: In Progress → In Review
                                                                              poll open PRs
                                         PR #N @ SHA #1 not yet reviewed ◄──── pick it up (it's "due")
                                                                              deterministic pre-checks:
                                                                                1. required PR-body sections
                                                                                2. diff not oversized
                                                                              then model-backed persona scan
                                         review posted (Approve /     ◄──────  submit verdict
                                          RequestChanges)
  WaitForReviewActivity polls PR
  ├─ RequestChanges:
  │    ModifyCodeActivity (agent fixes)
  │    └─ push ──────────────────────►  PR head moves to SHA #2 ────────────► next poll: SHA #2 unreviewed
  │                                                                            → re-review (loop)
  └─ Approved + Merged:
       (a human merges the approved PR ─►  PR merged)
       DoneActivity → move In Review → Done
       CompactMemory + cleanup
```

Key consequences of this design:

- **Idempotency is keyed on `(PR number, head SHA, reviewer login)`.** The reviewer reviews each
  head SHA exactly once; when the developer pushes a fix the head SHA changes, so the same PR
  becomes "due" again and is re-reviewed. A restart of either service re-derives its work from
  GitHub — neither holds the handshake in private state.
- **Approval alone does not advance the developer.** The review loop completes only on the
  *merged* state (`Merged && Approved`); an approved-but-unmerged PR keeps looping on the
  1-minute cadence timer until a human merges it.
- **`RequestChanges` is the iteration signal.** It drives `ModifyCodeActivity` (a fresh agent
  round against the same branch) and a new head SHA, which re-arms the reviewer.

> Note: the reviewer **persona** (`personas/reviewer.md`) describes a richer three-verdict model
> (`Approved` / `ChangesRequested` / `RejectedBlocking`). That text shapes the model's *reasoning*;
> the *implemented* verdict surface posted to GitHub is the binary Approve / RequestChanges gate
> above (`ReviewVerdict` collapses a block into `RequestChanges`).

## Reviewer Agent Low Level Design

### Core responsibility split

#### Frameworks split

Microsoft Agent Framework = reviewer agent abstraction, Anthropic connector, `submit_review` tool
.NET Generic Host (ASP.NET Core) = stateless background polling loop — **no Dapr, no Redis**
GitHub REST/GraphQL = open-PR listing, diff/body fetch, prior-review lookup, verdict submission

Unlike the DeveloperAgent, the ReviewerAgent holds **no durable runtime state**. It is a plain
web service whose `ReviewLifecycleService` (a `BackgroundService`) reconciles against GitHub on
every tick — there are **no Dapr Workflows, Actors, or workflow/actor state**, because GitHub is
the sole record of what has been reviewed and a restart re-derives the work. It also uses **no
MCP**: the reviewer fetches everything it needs (PR body + unified diff + change counts) in a
single `GetPullRequestForReviewAsync` round-trip and exposes exactly one tool (`submit_review`,
`AllowMultipleToolCalls = false`) to the model.

#### Components split

| Layer                           | Responsibility                                                            |
| ------------------------------- | ------------------------------------------------------------------------- |
| Microsoft Agent Framework       | Creates the Claude-powered reviewer agent                                 |
| Anthropic provider              | Connects the agent to Claude / `claude-opus-4-8` for the persona scan     |
| Reviewer persona                | System prompt (`personas/reviewer.md`) shaping the model's review reasoning |
| `submit_review` tool            | The only tool exposed to the model; records one verdict + markdown summary |
| Deterministic pre-checks        | Required-PR-body-sections + oversized-diff guards — run before any model call |
| `ReviewLifecycleService`        | Background poller: sweeps open PRs every `PollIntervalSeconds`             |
| Idempotency key                 | `(PR number, head SHA, reviewer login)` — each head SHA reviewed once      |
| GitHub REST/GraphQL service     | List open PRs, fetch body+diff, read prior reviewed SHAs, submit verdict   |
| Egress allow-list + resilience  | `HostAllowlistHandler` + standard resilience on the Anthropic/GitHub clients |
| HTTP endpoint                   | `POST /review/{prNumber}` for on-demand manual review                      |

### Review decision pipeline

Each open PR found by a sweep runs through an ordered gate; the first gate that fires wins, and
the model is reached only if both deterministic pre-checks pass:

1. **Due check** — skip if a draft (when `SkipDrafts`), if the author is outside a non-empty
   `AuthorAllowList`, or if the current head SHA was already reviewed by `ReviewerLogin`.
2. **Deterministic check 1 — required sections.** The PR body must contain every header in
   `RequiredPrBodySections`, in order, with non-empty content. Otherwise → `RequestChanges`.
3. **Deterministic check 2 — diff size.** Reject if `ChangedFiles > MaxDiffFiles` or
   `ChangedLines > MaxDiffLines` ("split into smaller PRs"). Otherwise → `RequestChanges`.
4. **Persona scan.** The agent reads the full diff and calls `submit_review` exactly once →
   `Approve` or `RequestChanges`.
5. **Fail closed.** Any internal error during the scan, or the model never calling
   `submit_review`, yields `RequestChanges` with an explanatory summary.
6. **Post one review** via `SubmitReviewAsync` (Octokit). The reviewer **never merges**.

### Configuration

The reviewer's config is split across two sections — **the same `Agent` settings structure the
DeveloperAgent host uses**, plus a `Reviewer` section for the review-specific knobs:

- **`Agent`** — agent identity and runtime: `Name`, `Model`, `Effort`, `PersonaPath`,
  `PollIntervalSeconds` (mirrors the developer host; `ReviewPollIntervalSeconds` /
  `FirstRetryIntervalSeconds` are present for structural parity but unused by the stateless
  reviewer). Bound to a `ReviewerAgent.Configuration.AgentOptions` record; `Model`/`PersonaPath`
  are also bound onto the engine's `ReviewerOptions` and `PollIntervalSeconds` onto
  `ReviewPollingOptions` from these same keys.
- **`Reviewer`** — review-specific: the oversized-diff caps, required PR-body sections, and the
  idempotency/draft/author filters.

Defaults below are the canonical values from `src/ReviewerAgent/appsettings.json`:

```json
{
  "Agent": {
    "Name": "ReviewerAgent",
    "Model": "claude-opus-4-8",
    "Effort": "xhigh",
    "PersonaPath": "personas/reviewer.md",
    "PollIntervalSeconds": 60,
    "ReviewPollIntervalSeconds": 60,
    "FirstRetryIntervalSeconds": 2
  },
  "Reviewer": {
    "MaxDiffFiles": 50,
    "MaxDiffLines": 2000,
    "RequiredPrBodySections": [
      "## Summary",
      "## User-visible behavior",
      "## Tests/validation run",
      "## Notes/assumptions"
    ],
    "ReviewerLogin": "",
    "SkipDrafts": true,
    "AuthorAllowList": []
  }
}
```

> **`Effort` is not yet wired into the model call** — it is configured (matching the developer
> host's `Agent` section) but, as on the DeveloperAgent, does not currently change model-reasoning
> depth.

> **`ReviewerLogin` must be set** to the GitHub login the reviewer's token authenticates as.
> Left blank, idempotency is disabled (no prior review matches), so every sweep re-reviews and
> re-posts on every open PR — the service logs a loud warning at startup when it is empty.

The service binds Kestrel to `http://*:8090` (the DeveloperAgent uses `8089`).

## Developer Agent Low Level Design

### Core responsibility split

#### Frameworks split

Microsoft Agent Framework = agent abstraction, Anthropic connector, MCP tools
Dapr Workflow             = durable long-running programming-task lifecycle
Dapr Actors               = small stateful coordination actions
Dapr State + Redis        = runtime state, sessions, task state, actor state, workflow state

#### Components split

| Layer                           | Responsibility                                                |
| ------------------------------- | ------------------------------------------------------------- |
| Microsoft Agent Framework       | Creates the Claude-powered developer agent                    |
| Anthropic provider              | Connects the agent to Claude / `claude-opus-4-7`              |
| MCP C# SDK                      | Connects to GitHub MCP and Context7 MCP                       |
| Agent Framework MCP integration | Converts MCP tools into `AITool` objects                      |
| Dapr Workflow                   | Owns the long-running task lifecycle                          |
| Dapr Actor                      | Owns one small stateful unit, usually one GitHub Project item |
| Dapr State API                  | Stores agent/session/task state                               |
| Redis                           | Physical backing store for Dapr state, actors, and workflow   |
| GitHub GraphQL/REST service     | Deterministic project/PR operations                           |
| Build/test runner               | Executes `dotnet restore`, `dotnet build`, `dotnet test`      |
| Policy engine                   | Controls what the agent is allowed to do                      |
