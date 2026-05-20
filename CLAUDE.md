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
