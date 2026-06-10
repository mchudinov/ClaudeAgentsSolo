# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

A .NET 10 / .NET Aspire scaffold for **DeveloperAgent** — an autonomous "C# developer" agent powered by Claude (`claude-opus-4-7`) that picks up GitHub Project items in `Ready` state, implements them, opens PRs, and moves items to `Done` after merge.

`docs/low_level_design_software.md` and `docs/idea.md` are the canonical design (Microsoft Agent Framework + Anthropic provider + MCP for GitHub/Context7 + Dapr Workflows/Actors/state on Redis + Octokit/Octokit.GraphQL); `docs/plans/` tracks the build. Phase 1 and Phase 2 are implemented — the lifecycle loop, Dapr workflow + actors, the developer and reviewer agents, the sandbox, the operator dashboard, and OTel metrics all exist and are covered by tests. The reusable, **agent-neutral** mechanics have since been progressively extracted into eight libraries under `src/AgenticTools/` (GitHub access, MCP tools, memory, the Anthropic runtime, the sandbox, file tools, workspace/git, workflow inspection). `DeveloperAgent` is now the **host** that wires those libraries together and owns the developer-agent *policy* (lifecycle, Dapr workflow + actors, the agents, the dashboard). See "Solution layout".

## Solution layout

Solution file is **`src/ClaudeAgentsSolo.slnx`** (the new XML solution format, not `.sln`). The organizing principle: **`src/AgenticTools/*` are reusable, agent-neutral *mechanics* with no dependency on `DeveloperAgent`; `DeveloperAgent` is the host that wires them together and owns the developer-agent *policy*** (lifecycle, workflow, actors, agents, dashboard, config, secrets). The "GitHub access layer" and "Memory layer" sections below are the two worked examples of this generic-mechanics-vs-policy split — every `AgenticTools/` library follows the same shape.

**Host & infrastructure projects** (top level of `src/`):

- **`AppHost/`** — Aspire orchestrator (`Aspire.AppHost.Sdk/13.1.0`). Entry point for local runs; orchestrates `DeveloperAgent` plus the Redis + Dapr resources.
- **`DeveloperAgent/`** — `Microsoft.NET.Sdk.Web` Blazor Server app, the agent **host**: the lifecycle loop (`Lifecycle/`), Dapr workflow + activities (`Workflow/`), the `ProgrammingTaskActor` (`Actors/`), the developer/reviewer agents (`Agent/`), the `GitHubProjectService` policy facade (`GitHub/`), options + secrets (`Configuration/`), the operator dashboard (`Dashboard/`), and OTel metrics (`Observability/`). Contains `Dockerfile`, `appsettings.json`, `Components/` (Razor), `MudBlazor`. References all eight `AgenticTools/` libraries plus `Library` + `ServiceDefaults`; surfaces most of their namespaces via `GlobalUsings.cs`.
- **`Library/`** — shared helpers: configuration key dumping, env-var logging, an `AddOpenTelemetry` builder extension (Azure Monitor), and `MapDefaultEndpoints` (`/livez`, `/uptime`, `/error`).
- **`ServiceDefaults/`** — standard Aspire service defaults project (`IsAspireSharedProject=true`): OTel, health checks (`/health`, `/alive`), service discovery, resilient HTTP. Reference this from any new service project via `builder.AddServiceDefaults()`.

**Agent-neutral libraries** (`src/AgenticTools/`, none referencing `DeveloperAgent`, each with an `Add*Services` DI entry point):

- **`Agent.GitHub/`** — GitHub Projects v2 + PR access, keyed by **status-column name** (Octokit + Octokit.GraphQL). See "GitHub access layer".
- **`Agent.Memory/`** — chat-history/repo/task/session stores + MAF context providers over a Dapr seam. See "Memory layer".
- **`Agent.Mcp/`** — stdio MCP tool source (`IMcpToolSource`/`McpToolSource`, `McpOptions`, `AddMcpServices`) that turns MCP servers launched via `npx` into MAF `AITool`s.
- **`Agent.Runtime/`** — the Anthropic chat-client factory (`IAgentChatClientFactory`), the turn/tool-call **cap decorator** (`TurnCountingChatClient`, `RunCounters`, `HardCapReachedException`/`ToolCallLimitReachedException`), and `PersonaLoader`. `AddAgentRuntimeServices`.
- **`Agent.Tools/`** — agent-neutral **file tools** (`ReadFileTool`/`WriteFileTool`/`EditFileTool`/`ListDirectoryTool`), the `ITool`/`IToolContext`/`IToolCallBudget`/`IPathDenyPolicy` seams, `PathValidator`, and `MafToolAdapter`. The base of the tool stack — **deliberately has no reference to `Agent.Sandbox`.**
- **`Agent.Sandbox/`** — command/file/egress sandbox: `ICommandSandbox`/`CommandSandbox`, `ShellRunTool`, deny policies (`CommandDenyPolicy`/`PathDenyPolicy`), `HostAllowlistHandler` (egress allow-list), `IContainerRuntime`/`DockerContainerRuntime`, and the `SandboxOptions`/`WorkspaceOptions`/`ContainerRuntimeOptions` records. `AddSandboxServices`. **Depends on `Agent.Tools`.**
- **`Agent.Workspace/`** — task workspace + git: `IWorkspaceManager`/`WorkspaceManager`, `IGitClient`/`GitClient`, `BranchName`, diff-scope guards (`DiffScopeLimitOptions`/`DiffStats`/`ScopeLimitExceededException`), `WorkspaceRootOptions`. `AddWorkspaceGitServices`. **Depends on `Agent.Sandbox`.**
- **`Agent.Workflow/`** — Dapr workflow-instance inspection (`IWorkflowInstanceInspector`/`DaprWorkflowInstanceInspector`). `AddWorkflowInspector`.

Dependency edges between the libraries: **`Agent.Tools` ← `Agent.Sandbox` ← `Agent.Workspace`**; the other five are standalone. None reference `DeveloperAgent`.

Tests live under **`src/Tests/`** — one project per library plus the host's: `Agent.GitHub.Tests`, `Agent.Mcp.Tests`, `Agent.Memory.Tests`, `Agent.Runtime.Tests`, `Agent.Sandbox.Tests`, `Agent.Tools.Tests`, `Agent.Workflow.Tests`, `Agent.Workspace.Tests`, and **`DeveloperAgent.Tests/`** (host: lifecycle, workflow, agents, dashboard, the GitHub facade, **and the Dapr round-trip integration tests**). The memory subsystem's Dapr **integration** round-trips stay in `DeveloperAgent.Tests/Integration/` — they exercise the host's `agent-state-store` component wiring and depend on its `EnvironmentSkip`/`SkippableFact` infra. Integration tests carry `[Trait("Category", "Integration")]` and are excluded by the fast filter (`--filter "Category!=Integration"`); a convention test enforces the trait. CI lives in `.github/workflows/`.

## Gotchas to know before editing

- **Generic mechanics vs. policy is the line for *all* new code, not just GitHub.** A reusable GitHub-API/MCP/memory/runtime/sandbox/tool/workspace/workflow capability → the matching `AgenticTools/` library (keep it agent-neutral). Developer-agent lifecycle, board-shape, or PR-template behavior → `DeveloperAgent`. For GitHub specifically: `Agent.GitHub` holds the status-name-keyed `IGitHubProjectsClient`, transports, and PR models; `DeveloperAgent` keeps the `ProjectState` enum, the `GitHubProjectService` facade, `ProjectStateNames`, and the §9 `PullRequestBodyBuilder`. See "GitHub access layer".
- **Configuration is split across projects; there is no monolithic `Settings.cs`.** Host-policy options records live in `DeveloperAgent/Configuration/` (`AnthropicOptions`, `AgentOptions`, `ReviewerOptions`, `ScopeLimitOptions`, `ProjectStateNames`, the `SecretsBundle*` providers, …). Library-owned options records live with their library: `SandboxOptions`/`WorkspaceOptions`/`ContainerRuntimeOptions` in `Agent.Sandbox`, `McpOptions` in `Agent.Mcp`, `DiffScopeLimitOptions`/`WorkspaceRootOptions` in `Agent.Workspace`, and GitHub identity (`GitHubOptions`/`RepositoryOptions`/`ProjectOptions`) in `Agent.GitHub`. All bind from `DeveloperAgent`'s `appsettings.json`, so a record and the config section that fills it now live in **different projects** — keep them in sync.
- **Don't seed list/array option defaults in the records.** The `ConfigurationBinder` *appends* a bound array onto whatever the property already holds (true even for `IReadOnlyList<T>` — it seeds a fresh list from the existing value, then adds the config children), so a non-empty C# default plus the same list in `appsettings.json` loads **twice** (Step-41 fixed exactly this: 38 deny rules instead of 19). The sandbox/workspace lists (`SandboxOptions.DeniedCommands`/`DenyPathPatterns`/`AllowedHosts`, `WorkspaceOptions.AllowedCommands`) therefore default to `[]` in their now-relocated `Agent.Sandbox` records and live **solely** in `DeveloperAgent/appsettings.json`; `Program.cs` requires them non-empty via `ValidateOnStart` (fail-closed). Tests read the canonical lists through `ProductionSandboxConfig` (binds the live `appsettings.json`), not `new SandboxOptions()`.
- **Two `Extensions.cs` files, two different `MapDefaultEndpoints`.** `Library.Extensions.MapDefaultEndpoints(app, applicationStartTime)` maps `/livez`, `/uptime`, `/error`. `Microsoft.Extensions.Hosting.Extensions.MapDefaultEndpoints(app)` (in `ServiceDefaults`) maps `/health`, `/alive` (dev only). Both currently get used — pick the right one based on whether you want the Aspire defaults or the custom Library endpoints.
- **`.gitignore` excludes `.vscode/` and `appsettings.Development*.json`.** Local dev overrides won't be committed; don't add secrets to `appsettings.json`.
- **`Aspire.AppHost.Sdk/13.1.0` + `net10.0`.** Requires a current .NET 10 SDK; older SDKs cannot restore.

## GitHub access layer

Deterministic GitHub access is split across two assemblies along a **generic-mechanics vs. agent-policy** line:

- **`Agent.GitHub` (library, agent-neutral).** `IGitHubProjectsClient` is the public API: board operations keyed by **status-column name** (`TryGetNextItemInStatusAsync`, `MoveItemAsync(from, to)`, `GetItemsInStatusesAsync`, `GetItemCountInStatusAsync`) plus PR/comment/exists operations. Backed by `GitHubProjectsClient` over internal `IGraphQLTransport`/`IRestTransport` Octokit wrappers — **Octokit types never escape the transports.** Models: `ProjectBoardItem` (raw string `Status`), `PullRequest`, `PullRequestStatus`, `PullRequestReviewState`, `PullRequestReviewContext`, `ReviewVerdict`, `CreatePullRequest`. Config: `GitHubOptions`/`RepositoryOptions`/`ProjectOptions`. The host supplies two seams: `IGitHubTokenProvider` (the GitHub token) and an `Action<IHttpClientBuilder>` passed to `AddGitHubProjectServices(...)` that composes egress + resilience onto the `github-rest`/`github-graphql` named clients (`GitHubHttpClients`). The library carries **no** lifecycle or board-shape opinion, so other agents can reuse it.
- **`DeveloperAgent` (host, policy).** `GitHubProjectService` (implements the typed `IGitHubProjectService`) is the developer-agent facade: it owns the four-state `ProjectState` lifecycle, maps `ProjectState` ↔ column names via `ProjectStateNames` (bound from `GitHub:States`), and maps `ProjectBoardItem` → `ProjectItem`. `SecretsBundleGitHubTokenProvider` supplies the token; `HostAllowlistHandler` + `AddStandardResilienceHandler` are composed via the `AddGitHubProjectServices` callback in `Program.cs`. The §9 four-section PR body (`PullRequestBodyBuilder`, in `DeveloperAgent.Agent`) renders via the library's generic `MarkdownSectionBuilder`.

**Adding GitHub code:** a generic GitHub-API capability → `Agent.GitHub` (keep it status-name-keyed and policy-free). Developer-agent lifecycle or PR-template behavior → `DeveloperAgent`. App consumers inject the typed `IGitHubProjectService` facade and keep working in `ProjectState`; a `global using Agent.GitHub` in `DeveloperAgent` surfaces the library types without per-file `using`s.

## Memory layer

The agent-memory subsystem (LLD memory design) was extracted into the agent-neutral **`Agent.Memory`** library in Step-40, as a lift-and-shift (the repo/task memory model is shared by every coding agent here, so unlike GitHub's per-agent `ProjectState` there was no policy worth splitting out):

- **`Agent.Memory` (library, agent-neutral).** Three storage seams keyed by plain string ids — `IChatHistoryStore` (`chat-history:{agentId}:{projectItemId}`), `IAgentMemoryStore` (repo conventions `repo-state:{repoId}` + per-task lessons `task-memory:{projectItemId}`), and `IAgentSessionStore` (workflow-progress `agent-session:{agentId}:{projectItemId}`) — each with a Dapr-backed and an in-memory implementation over the `IDaprStateClient`/`DaprClientStateAdapter` seam. The two MAF providers (`DaprChatHistoryProvider` with rolling-window summarisation, `DaprAgentMemoryContextProvider` with inject-before/extract-after) carry the windowing/dedup/cap **policy**; the stores only round-trip. The `ISummarizer`/`IMemoryExtractor` bodies are host-supplied seams. `AddAgentMemoryServices(agentId, stateStoreName?)` registers the adapter + the three durable stores (`IAgentSessionStore`, `IAgentMemoryStore`, `IChatHistoryStore`) as singletons; the host must register a `DaprClient` (as `Program.cs` does). Records: `AgentSession`, `ExtractedMemories`.
- **`DeveloperAgent` (host).** Owns the consumers, not the library: the workflow body, the session activities (`Save`/`Load`/`DeleteAgentSessionActivity`), and `CompactMemoryActivity` (writes the post-Done `task-memory` summary). It registers `DaprClient` + calls `AddAgentMemoryServices(Environment.MachineName)`. **Step-31 (P2-G integration) wires the MAF providers into the agent:** `IAgentMemoryProviderFactory`/`AgentMemoryProviderFactory` builds the two providers per run with runtime ids (agentId = machine name, repoId from `GitHubOptions`, projectItemId) and `AnthropicAgentRunner` attaches them to both `ChatClientAgentOptions` slots — `ChatHistoryProvider` (singular) and `AIContextProviders` (list); `MemoryOptions` (`Memory` config section) gates it and sets the window sizes. The host wires non-LLM placeholder seam bodies (`PlaceholderSummarizer`, `NoOpMemoryExtractor`) — so chat-history windowing + memory **injection** are live (the latter surfaces what `CompactMemoryActivity` writes), while the LLM-backed summarizer/extractor bodies remain deferred. A `global using Agent.Memory` surfaces the library types.

**Adding memory code:** generic store/provider mechanics or a new durable namespace → `Agent.Memory` (keep it string-keyed and policy-free). Workflow activities, run-scoped wiring, or the LLM-backed seam bodies → `DeveloperAgent`. The memory **integration** round-trips (real Dapr) stay in `DeveloperAgent.Tests`; the library's unit tests live in `Agent.Memory.Tests`.

## Personas

`personas/developer.md` and `personas/reviewer.md` are markdown system prompts loaded into the agent at runtime (the LLD references `PersonaPath: "/personas/developer.md"`). Edit these to change agent behavior — they are runtime configuration, not docs.

## Commands

All `dotnet` commands target the `.slnx` in `src/`. From the repo root:

```powershell
# Restore / build / publish whole solution
dotnet restore src/ClaudeAgentsSolo.slnx
dotnet build   src/ClaudeAgentsSolo.slnx
dotnet build   src/ClaudeAgentsSolo.slnx -c Release

# Run the app via Aspire (orchestrates DeveloperAgent + future Dapr/Redis resources)
dotnet run --project src/AppHost/AppHost.csproj

# Run DeveloperAgent directly (bypasses Aspire — no service discovery / Dapr sidecar)
dotnet run --project src/DeveloperAgent/DeveloperAgent.csproj

# Build a single project
dotnet build src/DeveloperAgent/DeveloperAgent.csproj

# Tests. Integration tests need Dapr/Redis/network; exclude them for the fast loop:
dotnet test src/ClaudeAgentsSolo.slnx --filter "Category!=Integration"
dotnet test src/ClaudeAgentsSolo.slnx
dotnet test src/ClaudeAgentsSolo.slnx --filter "FullyQualifiedName~SomeTest"

# Container build (Linux target, exposes 8089)
docker build -f src/DeveloperAgent/Dockerfile -t developer-agent src
```

Endpoints exposed by `DeveloperAgent` when running: `/livez`, `/uptime`, `/info`, `/error`, plus Aspire's `/health` and `/alive` in Development. Default Kestrel binding: `http://*:8089`.

## When implementing from the design docs

The LLD prescribes specific libraries — use these names exactly when adding packages:

- **Microsoft Agent Framework** for the agent abstraction
- **Anthropic provider** + `claude-opus-4-7` (configurable effort, default `xhigh`)
- **MCP C# SDK** for GitHub MCP and Context7 MCP (both stdio transport via `npx`)
- **Octokit.GraphQL.NET** for deterministic GitHub Project/PR operations (NOT MCP for state transitions)
- **Dapr Workflow** — one workflow instance per GitHub item, ID `github-project-item-{itemId}`
- **Dapr Actors** — `ProgrammingTaskActor` per item, plus actor reminders for polling
- **Dapr state (Redis)** — key namespaces are spelled out in the LLD; follow them verbatim

The `Workspace.AllowedCommands` allowlist in the LLD is the sandbox contract — code that runs shell commands on the agent's behalf must enforce it.

## Development Rules

### Test-driven development

**Always write tests before production code.** Use the red-green-refactor cycle for every change under `src/`: write a failing xUnit test that describes the new behavior (or reproduces the bug), write the minimum code that makes it pass, then refactor while the test stays green. Tests live in `src/Tests/DeveloperAgent.Tests/` per `docs/plans/06-testing-strategy.md`; each phase-1 plan's `§Verification` section enumerates the cases that must exist for that slice. Use the `superpowers:test-driven-development` skill for the canonical workflow. Do not commit code without a corresponding new or updated test — "I'll add tests later" and "this is too simple to test" are not acceptable. Carve-outs: documentation-only changes, configuration values that do not change runtime behavior, one-off local scripts, and dependency bumps with no behavior change.

### Documentation lookups

**Always use the Context7 MCP server for any documentation search** — libraries, frameworks, SDKs, APIs, CLI tools, cloud services. This applies even to well-known names (ASP.NET Core, EF Core, Dapr, MudBlazor, Microsoft Agent Framework, Anthropic SDK, Octokit, MCP C# SDK, OpenTelemetry, Serilog, Aspire) and even when the answer feels obvious — training data may not reflect current APIs. Prefer Context7 over web search and over answering from memory. Do not use it for refactoring, business-logic debugging, or general programming concepts.

### Plan and immplement development steps

1. Plan all steps and create a GitHub project items in the ClaudeAgentsSolo project with title "Step-N Short description", a short description body, and status set to Backlog. Onew item for step.
2. Pick up first item that should be implemented and move it to "Ready" state.
3. Create a dedicated GitHub branch named after the step (e.g. Step-1-Create-AccountSnapshot-data-model)
4. Move the corresponding GitHub project item to "In-progress" state
5. Do all coding on that branch

When programming is done:

1. Move the GitHub project item to "In-review" state
2. Do all unit tests. Fix tests if not green.
3. Merge to main if tests are all green.
4. Delete the local and remote feature branch, move the GitHub project item to "Done" state, then remind the user to run /compact.
5. Proceed to next step.

Use subagents when parallel programming is possible.
