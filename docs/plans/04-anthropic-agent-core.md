# 04 — Anthropic Agent Core

**Status:** draft — 2026-05-20
**Depends on:** `01-configuration-and-process-shape.md`, `02-github-octokit-service.md`, `03-workspace-git-and-sandbox.md`.
**Unblocks:** `05-lifecycle-loop.md`.

## Purpose

Owns the conversation with Claude: builds the request, registers the tool surface, runs the tool-use loop, returns when the model stops calling tools or the turn cap is hit. The lifecycle loop hands this layer one `TaskWorkspace` + the issue's title and body; the agent does everything from there (read/edit code, run build/tests, post comments, open the PR) using the tools below.

Phase 1 uses the **Anthropic .NET SDK directly** — `Anthropic.SDK` (the community client; verify the current package id and tool-use API via Context7 before pinning a version). No Microsoft Agent Framework, no MCP, no AIContextProvider. Phase 2 will swap this layer for Agent Framework + Anthropic provider + MCP tools without changing the public surface defined below.

## Deliverables

| Path | Change |
| ---- | ------ |
| `src/DeveloperAgent/DeveloperAgent.csproj` | Add `PackageReference Include="Anthropic.SDK"` (pinned version — confirm latest via Context7). |
| `src/DeveloperAgent/Agent/IAgentRunner.cs` | Public interface: run one task to completion. |
| `src/DeveloperAgent/Agent/AnthropicAgentRunner.cs` | Implementation. Owns the message loop. |
| `src/DeveloperAgent/Agent/Tools/` | One file per tool (`ReadFileTool.cs`, `WriteFileTool.cs`, `EditFileTool.cs`, `ListDirectoryTool.cs`, `ShellRunTool.cs`, `CommentOnItemTool.cs`, `CreatePullRequestTool.cs`). |
| `src/DeveloperAgent/Agent/Tools/ITool.cs` | Tool contract used by the runner. |
| `src/DeveloperAgent/Agent/AgentSession.cs` | In-memory session record (message history, counters). |
| `src/DeveloperAgent/Agent/PersonaLoader.cs` | Loads and caches the developer persona at startup. |
| `personas/developer.md` | Copy as content into `src/DeveloperAgent/personas/developer.md` and add `<Content Include="personas\developer.md" CopyToOutputDirectory="PreserveNewest"/>` to the csproj so it travels with the app. (Source of truth stays the top-level `personas/developer.md`; a build step or manual sync keeps the embedded copy current — see §Persona loading.) |
| `src/DeveloperAgent/Program.cs` | DI: `AddSingleton<IAgentRunner, AnthropicAgentRunner>()` and register every `ITool` implementation. |

## Public surface

```csharp
namespace DeveloperAgent.Agent;

public interface IAgentRunner
{
    Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct);
}

public sealed record AgentRunRequest(
    ProjectItem Item,             // from DeveloperAgent.GitHub
    TaskWorkspace Workspace,      // from DeveloperAgent.Workspace
    string? PriorReviewFeedback); // null on first round; populated with concatenated review comments on subsequent rounds

public sealed record AgentRunResult(
    AgentRunOutcome Outcome,
    PullRequest? PullRequest,     // populated if the agent successfully called the create_pull_request tool
    int TurnsUsed,
    int ToolCallsUsed,
    string? TerminationReason);   // human-readable, used in logs and item comments

public enum AgentRunOutcome
{
    Completed,            // model stopped producing tool calls
    HardCapReached,       // MaxModelTurnsHardCap hit; loop aborted
    SandboxViolation,     // a tool raised SandboxViolationException; task is dead
    ApiError,             // unrecoverable Anthropic API error after retries
    Cancelled
}
```

```csharp
namespace DeveloperAgent.Agent.Tools;

public interface ITool
{
    string Name { get; }                  // matches the model-visible tool name
    string Description { get; }           // shown to the model
    JsonNode InputSchema { get; }         // JSON Schema for tool_use.input
    Task<ToolResult> InvokeAsync(JsonNode input, ToolContext context, CancellationToken ct);
}

public sealed record ToolContext(
    AgentSession Session,
    TaskWorkspace Workspace,
    ProjectItem Item);

public sealed record ToolResult(
    bool IsError,
    string Content);                      // serialized back to the model as tool_result
```

## Tool surface (phase 1)

Tools the model sees and may call:

| Name | Purpose | Backed by |
| ---- | ------- | --------- |
| `read_file` | Read a UTF-8 text file under the workspace. Returns content. | `File.ReadAllTextAsync`, path validated against `Workspace.RepoRoot`. |
| `write_file` | Create or overwrite a file under the workspace. Parent dirs created. | `File.WriteAllTextAsync`, path validated. |
| `edit_file` | Replace exactly one occurrence of `old_string` with `new_string` in a file (matches `Edit` tool semantics from the persona's worldview). Fails if `old_string` is not unique or absent. | Read, validate, write. |
| `list_directory` | List entries under a workspace-relative path; optional `recursive` and `glob`. | `Directory.EnumerateFileSystemEntries`. |
| `shell_run` | Run a sandboxed command line in a workspace-relative `working_directory` (default = repo root). Returns `exit_code`, `stdout`, `stderr`, `timed_out`. | `ICommandSandbox.RunAsync` from plan 03. Default timeout 10 min; tool input may override down to ≥ 5 s and up to a hard ceiling of 20 min. |
| `comment_on_item` | Post a markdown comment on the active GitHub Project item. Used for the plan, status updates, blockers. | `IGitHubProjectService.AddItemCommentAsync`. |
| `create_pull_request` | Open a PR for the current workspace's branch into `Workspace.DefaultBranch`. Input is the four sections from persona §9; the title is built from the first non-empty sentence of `summary` truncated to 72 chars (model may also supply an explicit title that overrides). | `PullRequestBodyBuilder.Build` + `IGitHubProjectService.CreatePullRequestAsync`. |

What the model **cannot** do in phase 1:

- Make arbitrary HTTP requests. There is no `web_fetch` / `web_search` tool. The model uses its own knowledge plus the repo it cloned.
- Move project item state. The lifecycle loop owns that — the model only signals progress through comments and the PR.
- Force-push, rebase, branch-rename, anything not exposed as a tool. `shell_run` is gated by the allowlist; force-push and rebase are not allowlisted.
- Read or write outside the workspace. Every file-tool validates the resolved path against `Workspace.RepoRoot` (not the broader `RootPath`) so the agent stays in its own repo clone.
- Edit `personas/developer.md` (its own system prompt) — the persona file lives outside `Workspace.RepoRoot`, so the path validator already blocks it.

## Behavior

### Persona loading

`PersonaLoader` runs at startup (in `Program.cs`):

1. Resolve `AgentOptions.PersonaPath` relative to `IHostEnvironment.ContentRootPath`. If absolute, use as-is.
2. Read the file. If missing or empty, throw — the agent cannot operate without a persona.
3. Cache the text in a singleton.

The top-level `personas/developer.md` is the source of truth. The csproj copies it into the published output via a `<Content Include>` referencing the workspace-relative path; if the csproj's `<Content>` glob is brittle on the build host, document the alternative of a `cp` step in the README rather than maintaining two copies by hand.

### Tool registration with Anthropic

The runner constructs the `tools` array from every registered `ITool` (DI: `IEnumerable<ITool>`). Each tool's JSON schema becomes the `input_schema` field of the Anthropic tool definition. The persona is sent as the `system` parameter.

The kickoff user message contains, in this order:

```text
GitHub Project item: #{Item.ContentNumber} — {Item.Title}

Issue body:
{Item.BodyMarkdown}

Workspace root: {Workspace.RepoRoot}
Branch you must use: {Workspace.BranchName}
Default branch: {Workspace.DefaultBranch}

Prior reviewer feedback (if any):
{PriorReviewFeedback ?? "(none — this is the first round)"}
```

The model is expected to follow the persona's workflow §1–9 from here.

### The loop

```text
loop:
  request Claude with [system=persona, messages=session.history, tools=tools]
  on response:
    append assistant message to session.history
    if response has tool_use blocks:
      for each tool_use:
        result = tool.InvokeAsync(input, context, ct)
        append a user message containing tool_result(id=block.id, content=result.Content, is_error=result.IsError)
        session.ToolCallsUsed++
      session.TurnsUsed++
      if session.TurnsUsed > AgentOptions.MaxModelTurnsHardCap:
        return HardCapReached
      continue
    else:
      # plain assistant message, no tool calls — task completed
      session.FinalAssistantText = response.text
      return Completed
```

The hard cap is the only scope guard in phase 1 — full `MaxToolCalls` and time budgets land in phase 2.

### Model parameters

- `model` = `AgentOptions.Model` (default `claude-opus-4-7`).
- `max_tokens` = 32 000 (plenty for any single turn; tune later).
- `temperature` = 0 (deterministic-ish; agent work is best with low temperature).
- **Extended thinking / effort** — `AgentOptions.Effort` (`xhigh`, `high`, `medium`, `low`) maps to the SDK's extended-thinking budget parameter. The mapping table is implementation-defined; the implementer **must** confirm the current Anthropic API parameter name and value bounds via Context7 before wiring it. The default `xhigh` should set the highest available thinking budget.
- `tools` = the registered tool list.
- `system` = persona text.

### Error handling

- **`SandboxViolationException` from a tool** → end the run with `SandboxViolation`. Do not feed the violation back to the model; the violation itself plus a sanitized message goes to `TerminationReason` and the lifecycle loop comments on the item.
- **HTTP / API errors from Anthropic** — retry with exponential back-off (3 tries, 1 s / 4 s / 16 s) on `429`, `502`, `503`, `504`. On unrecoverable error after retries, end with `ApiError`. (Phase 2: replace this with proper Polly policies aligned to Dapr resiliency.)
- **`CancellationToken`** — the lifecycle loop cancels when shutting down. The runner stops the next iteration, captures the partial session in logs, and returns `Cancelled`.
- **Tool input validation failures** (bad path, malformed JSON) — fed back to the model as `is_error=true` tool_result so the model can correct itself. Validation errors are *not* sandbox violations.

### Session lifetime

`AgentSession` is created per `RunAsync` call, held in a local variable, and discarded when the method returns. No cross-call persistence in phase 1. The lifecycle loop optionally serialises the final session to `logs/session.json` for post-mortem inspection.

## Out of scope (deferred to phase 2)

- **MCP tools** — both GitHub MCP (replaces some Octokit calls inside the agent's reach) and Context7 MCP (gives the agent live docs lookup) are added to the tool list in phase 2 via Microsoft Agent Framework's MCP integration.
- **`DaprChatHistoryProvider`** — chat history stays in process memory.
- **`DaprAgentMemoryContextProvider`** — no learned-memory injection in phase 1.
- **Reviewer agent loop** — phase 2 adds an automated reviewer that consumes the PR diff and posts approve/request-changes; phase 1 review is purely human.
- **Configurable per-tool budgets / `MaxToolCalls` / time budget** — phase 1 has only `MaxModelTurnsHardCap`.
- **Tool-call streaming** — phase 1 uses non-streaming responses for simplicity.
- **Effort-level → thinking-budget table** finalisation — phase 1 wires the mapping behind one constant; phase 2 makes it explicit per-effort once Anthropic's API around extended thinking is consulted via Context7.
- **Image / PDF tool inputs** — text only.

## Verification

- Unit tests on each tool: path validation rejects escape attempts, `edit_file` rejects non-unique matches, `shell_run` rejects allowlist misses by delegating to the sandbox (mocked).
- Unit test on `AnthropicAgentRunner` with a fake `IAnthropicClient` returning canned responses:
  - tool_use → tool_result → text-only assistant → returns `Completed` with the right counters,
  - 41 successive tool calls → returns `HardCapReached`,
  - tool raising `SandboxViolationException` → returns `SandboxViolation`,
  - cancellation → returns `Cancelled`.
- An integration smoke test (opt-in via `ANTHROPIC_INTEGRATION_KEY`) prompts the real API with a trivial task ("add a line to README, then call create_pull_request") against a throwaway repo, and asserts the PR is opened with the four-section body.
