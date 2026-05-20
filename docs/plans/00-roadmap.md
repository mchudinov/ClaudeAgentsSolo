# 00 — Implementation Roadmap

**Status:** draft — 2026-05-20
**Predecessors:** `docs/idea.md`, `docs/low_level_design_software.md`, `personas/developer.md`

## Goal

Turn the existing Aspire + Blazor scaffold into a **walking-skeleton DeveloperAgent** that can pick a single GitHub Project item in `Ready`, branch, edit, build/test, push, open a PR, wait for human approval, and move the item to `Done` — single-replica, in-memory state, no Dapr, no MCP, no advanced memory. Phase 2 adds durability and the rest of the LLD.

The walking skeleton's success criterion: an operator can put one trivial issue into `Ready`, start the agent, and within a few minutes see a PR open, a comment posted on the item, and (after the operator approves and merges the PR) the item moves to `Done`.

## Phase 1 — walking skeleton

| # | Plan | Owns |
| - | ---- | ---- |
| 01 | `01-configuration-and-process-shape.md` | `Settings.cs` rewrite, `appsettings.json` schema (`Agent`/`Anthropic`/`GitHub`/`Workspace`), Web SDK + `IHostedService` shape, secret handling |
| 02 | `02-github-octokit-service.md` | Octokit.GraphQL.NET client, project state transitions, PR creation, approval polling, PR body builder, item comments |
| 03 | `03-workspace-git-and-sandbox.md` | Workspace dir, repo clone, `git` CLI wrapper, command allowlist, branch naming, push protection |
| 04 | `04-anthropic-agent-core.md` | Anthropic .NET SDK setup, persona loading, tool-use loop, allowlisted file/shell tools, turn/tool-call guards, in-memory session |
| 05 | `05-lifecycle-loop.md` | `IHostedService` poll loop, per-item state machine (Plan → Branch → Modify → Build → Test → Commit → Push → PR → Wait → Done), error boundaries |
| 06 | `06-testing-strategy.md` | `tests/` layout, xUnit + FluentAssertions, in-memory GitHub stub, what's unit vs integration |

### Sequencing

```text
01 (config + process shape)
   │
   ├──► 02 (GitHub service)
   │       │
   │       └─────────────────┐
   │                         │
   ├──► 03 (workspace+git)   │
   │       │                 │
   │       └─────────┐       │
   │                 │       │
   └──► 04 (agent core)──────┤
                     │       │
                     └──► 05 (lifecycle loop) ──► 06 (tests)
```

`01` unblocks everything else. `02`, `03`, `04` can be built in parallel once config is stable; they merge in `05`, which is then verified by `06`.

### What is explicitly OUT of phase 1

These belong to `07-phase-2-outline.md` and must not leak into phase-1 work:

- **Dapr Workflow** (`DeveloperTaskWorkflow`), **Dapr Actor** (`ProgrammingTaskActor`), **Dapr state store** (Redis). The phase-1 loop is a plain `IHostedService` with in-memory state — a process restart loses progress and that is acceptable.
- **MCP servers** — no GitHub MCP, no Context7 MCP. The agent uses Octokit + the Anthropic SDK directly. (Context7 lookup is the developer-Claude-Code workflow when *implementing* the agent; the running agent itself does not use it in phase 1.)
- **Memory layers** — no `DaprChatHistoryProvider`, no `DaprAgentMemoryContextProvider`, no compacted task memory. The agent gets the persona, the issue body, and the per-item conversation as ephemeral context.
- **Reviewer agent** — `personas/reviewer.md` exists but is not wired in phase 1; review is a human.
- **Scope-limit enforcement** — no `MaxChangedFiles`, `MaxChangedLines`, `MaxModelTurns`, `MaxToolCalls` ceilings. Add a single hard-coded turn cap inside the agent loop as a safety net (see plan 04), but do not implement the configurable limits.
- **Sandbox deny rules and secret-file protection** — phase 1 enforces an *allowlist only*. Blocked operations (`~/.ssh`, `.env`, force push, branch-protection changes, arbitrary `curl`/`wget`) are deferred to phase 2's policy engine.
- **Recovery on startup** — on boot, log any items in `In Progress` / `In Review` and skip them rather than resume. Phase 2 wires recovery into the actor state.
- **Resiliency policies** — phase 1 uses framework defaults (Polly via `Microsoft.Extensions.Http.Resilience` for HTTP). No bespoke retry/circuit-breaker tuning.
- **Operator dashboard UI** — the Blazor/MudBlazor scaffold remains in place but the Razor pages stay as the default templates. Dashboard work is phase 2.

### Phase-1 acceptance criteria

The skeleton is done when, against a real test GitHub repo + project:

1. The agent starts, logs configuration, polls the project, and finds a single `Ready` item.
2. It moves the item to `In Progress`, clones the repo, creates an `agent/<name>` branch, applies a trivial edit (e.g. "add a comment to README"), runs `dotnet restore` → `dotnet build --no-restore` → `dotnet test --no-build` successfully against the target solution.
3. It commits and pushes the branch, opens a PR with the four-section body from `personas/developer.md` §9, comments the implementation plan on the item, and moves the item to `In Review`.
4. After the operator approves and merges the PR, the agent (still running) detects the merge within one poll cycle, moves the item to `Done`, and resumes polling.
5. The unit-test suite under `tests/` passes via `dotnet test src/ClaudeAgentsSolo.slnx`.

## Phase 2 — see `07-phase-2-outline.md`

Phase 2 layers Dapr Workflow + Actor + Redis state under the existing loop (replacing the in-memory state), introduces both MCP servers as `AITool` sources via Microsoft Agent Framework, builds the three memory layers, enforces scope limits and full sandbox deny rules, wires recovery from durable state on startup, adds resiliency policies on every external call, and builds the operator dashboard. The reviewer agent is also a phase-2 deliverable.

The phase-1 code is structured so phase 2 is *additive* wherever possible: interfaces (`IAgentSession`, `IGitHubProjectService`, `ITaskStateStore`) defined in phase 1 get new Dapr-backed implementations in phase 2 without rewriting the lifecycle loop.

## Plan-file conventions

Every numbered plan in this directory follows this structure:

1. **Purpose** — one paragraph: what this slice owns and why it exists.
2. **Deliverables** — concrete files added/edited, with paths under `src/`.
3. **Public surface** — C# interfaces and DTO shapes other plans depend on. These are the contract; private implementation is left to the implementer.
4. **Behavior** — sequence of operations, edge cases, error handling at this layer's boundary.
5. **Out of scope** — explicit list of things adjacent plans (or phase 2) own.
6. **Verification** — the smallest reproducible check that proves this slice works, before the lifecycle loop is wired up.

Plans link to each other by file name (e.g. "see `02-github-octokit-service.md` §Public surface"). Sections are numbered with `§<heading>` where useful.
