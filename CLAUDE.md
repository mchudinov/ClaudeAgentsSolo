# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

A .NET 10 / .NET Aspire scaffold for **DeveloperAgent** — an autonomous "C# developer" agent powered by Claude (`claude-opus-4-7`) that picks up GitHub Project items in `Ready` state, implements them, opens PRs, and moves items to `Done` after merge.

**The design docs are well ahead of the code.** `docs/low_level_design_software.md` and `docs/idea.md` describe a system built on Microsoft Agent Framework + Anthropic provider + MCP (GitHub, Context7) + Dapr Workflows + Dapr Actors + Dapr state (Redis) + Octokit.GraphQL. **None of that is wired up yet.** The current `src/` is an Aspire AppHost hosting a single empty Blazor Server app (`DeveloperAgent`) that still has Azure OpenAI placeholder settings. Treat the docs as the target architecture and the code as a fresh canvas — when implementing, expect to add packages and projects rather than refactor existing ones.

## Solution layout

Solution file is **`src/ClaudeAgentsSolo.slnx`** (the new XML solution format, not `.sln`). All four projects live under `src/`:

- **`AppHost/`** — Aspire orchestrator (`Aspire.AppHost.Sdk/13.1.0`). Entry point for local runs; currently registers `DeveloperAgent` as the resource named `"web"`.
- **`DeveloperAgent/`** — `Microsoft.NET.Sdk.Web` Blazor Server app, the future agent runtime. Contains `Dockerfile`, `appsettings.json`, `wwwroot/`, and `Components/` (Razor). Has a `MudBlazor` dependency.
- **`Library/`** — shared helpers: configuration key dumping, env-var logging, an `AddOpenTelemetry` builder extension (Azure Monitor), and `MapDefaultEndpoints` (`/livez`, `/uptime`, `/error`).
- **`ServiceDefaults/`** — standard Aspire service defaults project (`IsAspireSharedProject=true`): OTel, health checks (`/health`, `/alive`), service discovery, resilient HTTP. Reference this from any new service project via `builder.AddServiceDefaults()`.

`tests/` exists but is empty. `.github/workflows/` exists but is empty.

## Gotchas to know before editing

- **Namespace ≠ project name in `DeveloperAgent`.** `Program.cs` declares `namespace Web` and the class is `Web.Program`, while Razor components live under `DeveloperAgent.Components` (see `Components/App.razor` consumer in `Program.cs`). Be deliberate when adding files — don't blindly assume the namespace from the folder.
- **`Settings.cs` is stale vs. the design.** It models `AzureOpenAI` config; the LLD calls for `Anthropic` + `GitHub` + `McpServers` + `Dapr` + `Workspace` sections (see `docs/low_level_design_software.md` for the canonical schema). Updating `Settings.cs` and `appsettings.json` together is the first real coding task implied by the design.
- **Two `Extensions.cs` files, two different `MapDefaultEndpoints`.** `Library.Extensions.MapDefaultEndpoints(app, applicationStartTime)` maps `/livez`, `/uptime`, `/error`. `Microsoft.Extensions.Hosting.Extensions.MapDefaultEndpoints(app)` (in `ServiceDefaults`) maps `/health`, `/alive` (dev only). Both currently get used — pick the right one based on whether you want the Aspire defaults or the custom Library endpoints.
- **`.gitignore` excludes `.vscode/` and `appsettings.Development*.json`.** Local dev overrides won't be committed; don't add secrets to `appsettings.json`.
- **`Aspire.AppHost.Sdk/13.1.0` + `net10.0`.** Requires a current .NET 10 SDK; older SDKs cannot restore.

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

# Tests — `tests/` is empty today. Once test projects exist:
dotnet test src/ClaudeAgentsSolo.slnx
dotnet test --filter "FullyQualifiedName~SomeTest"

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

### Plan and immplement a Step

When implement a development step:

1. Create a GitHub project item in the ClaudeAgentsSolo project with title "Step-N Short description", a short description body, and status set to Backlog.
2. Create a dedicated GitHub branch named after the step (e.g. Step-1-Create-AccountSnapshot-data-model)
3. Move the corresponding GitHub project item to "In-progress" state
4. Do all coding on that branch

When programming is done:

1. Move the GitHub project item to "In-review" state
2. Do all unit tests. Fix tests if not green.
3. Merge to main if tests are all green.
4. Delete the local and remote feature branch, move the GitHub project item to "Done" state, then remind the user to run /compact.
