# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

A .NET 10 / .NET Aspire scaffold for **DeveloperAgent** — an autonomous "C# developer" agent powered by Claude (`claude-opus-4-7`) that picks up GitHub Project items in `Ready` state, implements them, opens PRs, and moves items to `Done` after merge.

`docs/low_level_design_software.md` and `docs/idea.md` are the canonical design (Microsoft Agent Framework + Anthropic provider + MCP for GitHub/Context7 + Dapr Workflows/Actors/state on Redis + Octokit/Octokit.GraphQL); `docs/plans/` tracks the build. Phase 1 and Phase 2 are implemented — the lifecycle loop, Dapr workflow + actors, the developer and reviewer agents, the sandbox, the operator dashboard, and OTel metrics all exist and are covered by tests. The deterministic GitHub access has since been extracted into a reusable, **agent-neutral** `Agent.GitHub` library (see "Solution layout" and "GitHub access layer").

## Solution layout

Solution file is **`src/ClaudeAgentsSolo.slnx`** (the new XML solution format, not `.sln`). Projects under `src/`:

- **`AppHost/`** — Aspire orchestrator (`Aspire.AppHost.Sdk/13.1.0`). Entry point for local runs; orchestrates `DeveloperAgent` plus the Redis + Dapr resources.
- **`DeveloperAgent/`** — `Microsoft.NET.Sdk.Web` Blazor Server app, the agent runtime (lifecycle loop, Dapr workflow + actors, developer + reviewer agents, sandbox, operator dashboard). Contains `Dockerfile`, `appsettings.json`, `Components/` (Razor); has a `MudBlazor` dependency. References `Agent.GitHub`.
- **`Agent.GitHub/`** — reusable, **agent-neutral** GitHub Projects v2 + pull-request access library (Octokit + Octokit.GraphQL). The deliverable of the GitHub extraction; see "GitHub access layer" below. Has **no** dependency on `DeveloperAgent`.
- **`Library/`** — shared helpers: configuration key dumping, env-var logging, an `AddOpenTelemetry` builder extension (Azure Monitor), and `MapDefaultEndpoints` (`/livez`, `/uptime`, `/error`).
- **`ServiceDefaults/`** — standard Aspire service defaults project (`IsAspireSharedProject=true`): OTel, health checks (`/health`, `/alive`), service discovery, resilient HTTP. Reference this from any new service project via `builder.AddServiceDefaults()`.

Tests under `tests/`: **`DeveloperAgent.Tests/`** (app: lifecycle, workflow, agents, sandbox, dashboard, the GitHub facade, integration) and **`Agent.GitHub.Tests/`** (the library's generic mechanics). Integration tests carry `[Trait("Category", "Integration")]` and are excluded by the fast filter (`--filter "Category!=Integration"`); a convention test enforces the trait. CI lives in `.github/workflows/`.

## Gotchas to know before editing

- **GitHub code: generic vs. policy.** Generic GitHub mechanics live in the `Agent.GitHub` library (status-name-keyed `IGitHubProjectsClient`, transports, PR models). Developer-agent policy stays in `DeveloperAgent`: the `ProjectState` enum, the `GitHubProjectService` facade, `ProjectStateNames`, and the §9 `PullRequestBodyBuilder`. Put new code on the correct side of that line — see "GitHub access layer".
- **Configuration lives in `DeveloperAgent/Configuration/` options records** (`AnthropicOptions`, `McpOptions`, `WorkspaceOptions`, `SandboxOptions`, `ScopeLimitOptions`, `ProjectStateNames`, …) bound from `appsettings.json`; GitHub identity (`GitHubOptions`/`RepositoryOptions`/`ProjectOptions`) now lives in `Agent.GitHub`. There is no monolithic `Settings.cs`.
- **Two `Extensions.cs` files, two different `MapDefaultEndpoints`.** `Library.Extensions.MapDefaultEndpoints(app, applicationStartTime)` maps `/livez`, `/uptime`, `/error`. `Microsoft.Extensions.Hosting.Extensions.MapDefaultEndpoints(app)` (in `ServiceDefaults`) maps `/health`, `/alive` (dev only). Both currently get used — pick the right one based on whether you want the Aspire defaults or the custom Library endpoints.
- **`.gitignore` excludes `.vscode/` and `appsettings.Development*.json`.** Local dev overrides won't be committed; don't add secrets to `appsettings.json`.
- **`Aspire.AppHost.Sdk/13.1.0` + `net10.0`.** Requires a current .NET 10 SDK; older SDKs cannot restore.

## GitHub access layer

Deterministic GitHub access is split across two assemblies along a **generic-mechanics vs. agent-policy** line:

- **`Agent.GitHub` (library, agent-neutral).** `IGitHubProjectsClient` is the public API: board operations keyed by **status-column name** (`TryGetNextItemInStatusAsync`, `MoveItemAsync(from, to)`, `GetItemsInStatusesAsync`, `GetItemCountInStatusAsync`) plus PR/comment/exists operations. Backed by `GitHubProjectsClient` over internal `IGraphQLTransport`/`IRestTransport` Octokit wrappers — **Octokit types never escape the transports.** Models: `ProjectBoardItem` (raw string `Status`), `PullRequest`, `PullRequestStatus`, `PullRequestReviewState`, `PullRequestReviewContext`, `ReviewVerdict`, `CreatePullRequest`. Config: `GitHubOptions`/`RepositoryOptions`/`ProjectOptions`. The host supplies two seams: `IGitHubTokenProvider` (the GitHub token) and an `Action<IHttpClientBuilder>` passed to `AddGitHubProjectServices(...)` that composes egress + resilience onto the `github-rest`/`github-graphql` named clients (`GitHubHttpClients`). The library carries **no** lifecycle or board-shape opinion, so other agents can reuse it.
- **`DeveloperAgent` (host, policy).** `GitHubProjectService` (implements the typed `IGitHubProjectService`) is the developer-agent facade: it owns the four-state `ProjectState` lifecycle, maps `ProjectState` ↔ column names via `ProjectStateNames` (bound from `GitHub:States`), and maps `ProjectBoardItem` → `ProjectItem`. `SecretsBundleGitHubTokenProvider` supplies the token; `HostAllowlistHandler` + `AddStandardResilienceHandler` are composed via the `AddGitHubProjectServices` callback in `Program.cs`. The §9 four-section PR body (`PullRequestBodyBuilder`, in `DeveloperAgent.Agent`) renders via the library's generic `MarkdownSectionBuilder`.

**Adding GitHub code:** a generic GitHub-API capability → `Agent.GitHub` (keep it status-name-keyed and policy-free). Developer-agent lifecycle or PR-template behavior → `DeveloperAgent`. App consumers inject the typed `IGitHubProjectService` facade and keep working in `ProjectState`; a `global using Agent.GitHub` in `DeveloperAgent` surfaces the library types without per-file `using`s.

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

**Always write tests before production code.** Use the red-green-refactor cycle for every change under `src/`: write a failing xUnit test that describes the new behavior (or reproduces the bug), write the minimum code that makes it pass, then refactor while the test stays green. Tests live in `tests/DeveloperAgent.Tests/` per `docs/plans/06-testing-strategy.md`; each phase-1 plan's `§Verification` section enumerates the cases that must exist for that slice. Use the `superpowers:test-driven-development` skill for the canonical workflow. Do not commit code without a corresponding new or updated test — "I'll add tests later" and "this is too simple to test" are not acceptable. Carve-outs: documentation-only changes, configuration values that do not change runtime behavior, one-off local scripts, and dependency bumps with no behavior change.

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
