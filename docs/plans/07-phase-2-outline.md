# 07 — Phase 2 Outline

**Status:** outline — 2026-05-20
**Predecessors:** `00-roadmap.md` (phase-1 scope contract), all of `01`–`06`.

## Purpose

Phase 1 (the walking skeleton) deliberately strips the LLD down to the loop that proves the design works. Phase 2 layers durability, intelligence, and safety on top, in the order below. Each section is a **placeholder plan** — when the time comes, expand it into its own `08-*.md`, `09-*.md`, … file using the conventions from `00-roadmap.md` §Plan-file conventions.

Phase 2 work is **additive wherever possible**: the phase-1 interfaces (`IAgentRunner`, `IGitHubProjectService`, `IWorkspaceManager`, `IGitClient`, `ICommandSandbox`, `ITaskStateStore`, `ISecretResolver`) keep their shape. New implementations slot in behind them; the lifecycle loop's *call sites* move only when the orchestrator itself is replaced by Dapr Workflow.

## Sequencing

The graph below is the suggested order; arrows are hard prerequisites.

```text
P2-A Infrastructure (Aspire + Dapr + Redis)
        │
        ├──► P2-B Durable state store (replace InMemoryTaskStateStore via Dapr Actor)
        │
        ├──► P2-C Recovery on startup
        │
        └──► P2-D Dapr Workflow (DeveloperTaskWorkflow) replaces TaskExecutor

P2-E Microsoft Agent Framework swap (replace Anthropic.SDK direct usage)
        │
        ├──► P2-F MCP servers (GitHub MCP + Context7 MCP)
        │
        └──► P2-G Memory layers (chat history + AI context provider)

P2-H Scope-limit enforcement       P2-I Full sandbox deny rules
P2-J Compaction after Done          P2-K Resiliency policies
P2-L Operator dashboard             P2-M Reviewer agent
P2-N CI workflows + observability metrics
```

P2-A unblocks the durable-state half (B/C/D). P2-E unblocks the agent-framework half (F/G). The two halves can proceed in parallel once their roots land. P2-H through P2-N are independent and can interleave freely.

## P2-A — Aspire + Dapr + Redis infrastructure

Add Redis and the Dapr sidecar to `AppHost/AppHost.cs` using `Aspire.Hosting.Dapr`. Configure the actor state store and the regular state store both pointing at Redis. Add the YAML / programmatic Dapr component definitions. Update the Dockerfile + dev-launch instructions.

**Why deferred:** the walking skeleton works without it. Bringing this in adds infra-management complexity that distracts from proving the loop.

**New deliverables (sketch):** `AppHost.cs` Aspire wiring for Redis and Dapr; `src/DeveloperAgent/dapr-components/agent-state.yaml`; an integration test that round-trips a key through Dapr state.

## P2-B — Durable task state via Dapr Actor

Implement `ProgrammingTaskActor` per LLD §Dapr Actors. Replace `InMemoryTaskStateStore` with a Dapr-Actor-backed `ITaskStateStore`. Per-item state survives container restart; two replicas of the agent cannot both claim the same item.

**Why deferred:** phase 1's single-replica in-memory store is adequate to demonstrate the loop.

## P2-C — Recovery on startup

On boot, replace the phase-1 "log and skip" path with: enumerate items in `InProgress` and `InReview`, read each actor's state, resume from the exact phase. PRs that have already been merged are completed; PRs still open re-enter the review-wait loop.

**Why deferred:** without durable state there is nothing to resume from.

## P2-D — Dapr Workflow (`DeveloperTaskWorkflow`)

Replace `TaskExecutor.RunAsync` with a Dapr Workflow. Each activity from the LLD becomes a workflow activity (`acquire task`, `create branch`, `run LLM planning`, `modify code`, `run build/tests`, `create PR`, `move to Done`, `compact memory`). The `ChangesRequested` external event becomes a workflow external event. Workflow instance ID = `github-project-item-{itemId}` per LLD.

`AgentLifecycleService` becomes a thin dispatcher that creates one workflow instance per `Ready` item and lets Dapr Workflow drive the rest. Cross-restart resume comes for free.

**Why deferred:** phase 1 keeps the orchestrator as plain code so the loop is easy to read and debug.

## P2-E — Microsoft Agent Framework swap

Replace `AnthropicAgentRunner`'s direct use of `Anthropic.SDK` with Microsoft Agent Framework + its Anthropic provider. The `IAgentRunner` interface stays the same. Persona is the agent's system prompt. Tools become `AITool` objects.

This is the prerequisite for MCP and for the proper context-provider hooks.

**Why deferred:** the direct SDK is simpler and avoids paying the MAF abstraction cost while there's no MCP and no context-provider plumbing.

## P2-F — MCP servers

Wire two MCP clients per LLD `McpServers`:

- **GitHub MCP** — gives the agent richer repo exploration (multi-file search, semantic queries on issues/PRs) without going through Octokit.
- **Context7 MCP** — gives the agent live library documentation lookups during implementation.

Microsoft Agent Framework's MCP integration converts MCP tools into `AITool`, so they join the existing tool list (or replace the equivalent Octokit-backed tools).

**Why deferred:** the direct Octokit path covers everything the walking skeleton needs; the agent doesn't need live doc lookup to make a one-line README change.

## P2-G — Memory layers

Build the three layers from LLD §Agent Framework memory design:

1. **AgentSession** — serialise to Dapr state under `agent-session:{agentId}:{projectItemId}`.
2. **`DaprChatHistoryProvider : ChatHistoryProvider`** — load/save message history under `chat-history:{agentId}:{projectItemId}`. Apply windowing / compaction so returned history stays inside the model's context window.
3. **`DaprAgentMemoryContextProvider : AIContextProvider`** — inject relevant memories before each model call (repo conventions, prior task lessons), save useful new memories after each run, persist under `repo-state:{repoId}` and `task-memory:{projectItemId}`.

**Why deferred:** phase 1 sessions live and die with the process. Anything the model learned about the repo is rebuilt from scratch on each run — adequate for a one-issue demo, not adequate at scale.

## P2-H — Scope-limit enforcement

Implement the LLD's task-scope limits as a real policy layer:

- `MaxChangedFiles` / `MaxChangedLines` — checked against `git diff --numstat` before allowing `git push`.
- `MaxExecutionTime` — per-task wall clock enforced by the workflow.
- `MaxModelTurns` / `MaxToolCalls` — replace phase-1's lone `MaxModelTurnsHardCap` constant.
- `MaxRetryCount` — actor-tracked, applied by the workflow's retry policy.
- `MaxPRSize` — composite of file/line counts; PR opening blocked if exceeded, with a comment explaining the breach.

**Why deferred:** phase-1 tasks are trivial enough that bare turn-cap is enough.

## P2-I — Full sandbox deny rules

Extend `ICommandSandbox` and the file-tool path validators with:

- **Path deny rules** — `~/.ssh`, `.env*`, `.git/config` writes, anywhere outside the workspace, any path matching a configurable secret-file regex list.
- **Command deny rules** — `curl`, `wget`, `chmod +x`, `git push --force`, `gh secret set`, anything that mutates CI secrets or branch protection.
- **Network egress filter** — outbound HTTP allowlisted to Anthropic, GitHub, and Context7 hosts.
- **Container isolation per task** — each `shell_run` runs in an isolated child container (Firecracker / runc) with the workspace bind-mounted read-write, the rest of the host read-only.

**Why deferred:** phase 1 runs on operator-trusted infra with allowlist-only and is acceptable as a demo. Production needs every line of this.

## P2-J — Compaction after `Done`

Per LLD step 15: after the item moves to `Done`, the workflow runs a compaction activity that summarises completed task, changed files, decisions, test results, unresolved risks, and saves the summary to `task-memory:{projectItemId}` for the AI-context provider to reuse.

**Why deferred:** the phase-1 agent has no memory layer to read from anyway.

## P2-K — Resiliency policies

Apply Dapr resiliency (timeouts, retries with back-off, circuit breakers) on:

- Anthropic API calls
- GitHub REST + GraphQL calls
- MCP tool calls
- Dapr state-store operations

Replace the ad-hoc retry constants inside `AnthropicAgentRunner` with the Dapr resiliency layer.

**Why deferred:** Polly defaults (`Microsoft.Extensions.Http.Resilience`) are good enough for the demo. Tuned policies matter once the agent is running 24/7.

## P2-L — Operator dashboard

Build out the Blazor / MudBlazor scaffold that has been sitting empty since phase 1. Dashboard pages: current task, phase, branch, PR, last N log entries, manual pause / resume / cancel actions. Backed by `ITaskStateStore` (now Dapr-Actor-backed) and Serilog sink reading recent entries.

**Why deferred:** the loop works without a UI. The `/info` endpoint exposes enough state for a developer-operator.

## P2-M — Reviewer agent

`personas/reviewer.md` becomes a real agent. Replaces (or augments) the human reviewer:

- Watches PRs opened by `DeveloperAgent` for the configured repo.
- Pulls the diff, runs the four-section body checks, scans for the persona violations the developer agent should have caught.
- Posts an approval or `request_changes` review back through the Octokit service.

**Why deferred:** humans are the reviewers in phase 1. The reviewer agent doubles the complexity and is independently valuable enough to merit its own design pass.

## P2-N — CI workflows + observability metrics

- **CI** — populate `.github/workflows/`: a `dotnet test` workflow on PR (filtering out `Category=Integration`), a release workflow that builds and pushes the `DeveloperAgent` container image.
- **Metrics** — agent-specific counters and histograms (tasks per hour, tool calls per task, model tokens per task, build/test pass rate, time-to-PR). Emit via OpenTelemetry (the plumbing is already in `ServiceDefaults`); ship to whatever collector the operator wires up (`OTEL_EXPORTER_OTLP_ENDPOINT` is already honoured).

**Why deferred:** these are good-citizen items that don't change behaviour. Pile them in once the behaviour is stable.

## Things explicitly *not* on the phase-2 list

- **Multi-repo per agent process** — the LLD scopes one agent to one repository. If multi-repo support is desired, it is a phase-3 design.
- **Multiple concurrent agents on the same item** — Dapr Actor prevents this; the goal stays one agent per item.
- **Self-hosted runner / GitHub App auth flow** — phase 2 still uses PAT-style secrets via `ISecretResolver`. GitHub App auth is a separate hardening pass.
- **Agent self-update** — out of scope at every phase. Operator upgrades the container.
