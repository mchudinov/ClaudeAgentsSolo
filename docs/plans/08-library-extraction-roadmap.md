# 08 — Library Extraction Roadmap

**Status:** draft — 2026-06-05
**Predecessors:** the completed `Agent.GitHub` extraction (Steps 30s) and `Agent.Memory` extraction (Step-40), which established the **generic-mechanics vs. agent-policy** split pattern this roadmap applies to the rest of `DeveloperAgent`.
**Scope:** report/plan only — no code is extracted by this document. Each numbered step below is a separate, shippable slice to be executed under the standard repo flow (GitHub item → branch → In-progress → TDD → PR → tests green → merge → Done).

## How this was produced

A 15-agent audit swept the seven `DeveloperAgent` subsystem clusters (sandbox, workspace/git, MCP, tools, runtime, cross-cutting helpers, lifecycle/workflow). Each cluster was surveyed for its outward edges and then **adversarially challenged** for hidden coupling, dependency cycles, and `.csproj` that would not actually compile. The bar applied throughout is the one the two existing libraries already pass:

> **The extractability test.** Can every outward dependency of a cluster be resolved to (a) a generic NuGet package, (b) a host-supplied seam (an interface the host implements, e.g. `IGitHubTokenProvider`), or (c) another extractable library — **without referencing any `DeveloperAgent` policy type** (`ProjectState`, `ProjectItem`, the Plan/Build/Test/PR phases, the reviewer verdict, `SecretsBundle`, `OperatorCommand`, …)? "Is it generic?" is **not** the test; "can you write the `.csproj`?" is.

Three survey verdicts were corrected by the challenge pass; those corrections are called out inline below.

---

## 1. Executive summary

Eight genuine reusable units surfaced (out of eleven candidates; the rest are correctly policy that stays). The **highest-value candidate is `Agent.Sandbox`** — the allowlist + command-deny + path-deny + egress-handler + Docker-isolation security boundary that every coding agent running LLM-driven shell/file edits needs. It is also the **load-bearing root of the dependency DAG** (`Agent.Workspace`, `ShellRunTool`, and the file tools all sit on it), so it anchors the ordering.

`Agent.Mcp`, `Agent.Runtime`, `Agent.Workflow`, and three small shared-project moves carry **zero cross-candidate edges** and can be extracted in parallel with no prerequisites. The lifecycle / reviewer / GitHub-tool / metrics policy stays in `DeveloperAgent`, exactly as the GitHub and Memory splits kept their policy shells.

---

## 2. Ranked candidate table

Verdicts reflect the **corrected** outcome where the adversarial pass overrode the survey.

| # | Candidate | Target assembly | Verdict | Effort | Reuse value |
|---|-----------|-----------------|---------|--------|-------------|
| 1 | **Sandbox** (`ICommandSandbox`/`CommandSandbox`, command-deny, path-deny, `HostAllowlistHandler`, `IContainerRuntime`/`DockerContainerRuntime`, `SandboxViolationException`, `CommandResult`, config records) | **`Agent.Sandbox`** (new) | **ready** | M | **Highest** — security boundary every shell/file-editing agent needs |
| 2 | **MCP stdio tool source** (`IMcpToolSource`/`McpToolSource`, `IMcpClientConnector`/`StdioMcpClientConnector`, `McpOptions`) | **`Agent.Mcp`** (new) | **ready** | S | High — any MAF agent drops in stdio MCP servers via appsettings |
| 3 | **Anthropic chat-client factory** (`IAgentChatClientFactory`/`AnthropicChatClientFactory`) | **`Agent.Runtime`** (new) | **ready** | M | High — resilient `IChatClient` transport for any Anthropic agent |
| 4 | **Turn/tool-call cap decorator** (`TurnCountingChatClient` + `HardCapReachedException` + `ToolCallLimitReachedException`) | **`Agent.Runtime`** (new) | **ready** (needs counter-DTO refactor) | S | High — generic "cap the loop at N turns/tool-calls" safety mechanic |
| 5 | **Persona loader** (`PersonaLoader` string-ctor only) | **`Agent.Runtime`** (new) | **ready** | S | Medium — load a markdown system prompt, fail-fast if missing |
| 6 | **Workflow-instance inspector** (`IWorkflowInstanceInspector`/`DaprWorkflowInstanceInspector` + `WorkflowInstanceDisposition`) | **`Agent.Workflow`** (new) | **ready** | S | Medium — idempotent per-id Dapr-Workflow schedule probe |
| 7 | **File-tool core** (`ITool`/`ToolResult`/`PathValidator` + `ReadFileTool`/`WriteFileTool`/`EditFileTool`/`ListDirectoryTool` + `MafToolAdapter`) | **`Agent.Tools`** (new) | **ready-with-seam-refactor** ⚠️ *corrected from survey's "blocked"* | M | High — file-system tool kit + `ITool→AIFunction` bridge for any MAF agent |
| 8 | **Git client + workspace manager** (`IGitClient`/`GitClient`, `IWorkspaceManager`/`WorkspaceManager`, `ScopeLimitExceededException`, `TaskWorkspace`/`DiffStats`) | **`Agent.Workspace`** (new) | **blocked-on Sandbox seam + 2 config splits** | M | High — clone/branch/commit/push + diff-scope governance |
| 9 | **`ShellRunTool`** | **`Agent.Sandbox`** (move with sandbox) | **belongs-with Sandbox** ⚠️ *corrected: belongs with cluster A, not Tools* | S | Medium — thin generic adapter over the sandbox |
| 10 | **`BranchName`** (ref-safe branch builder) | **`Agent.Workspace`** (preferred) *not* `Library` ⚠️ | **ready** | S | Medium — deterministic git-ref-safe branch name builder |
| 11 | **Secret resolver** (`ISecretResolver` + `EnvAndUserSecretsResolver`) | **`Library`** | **belongs-in-shared** | S | High — named-secret resolution (user-secrets→env→throw) |
| 12 | **Recent-log ring buffer** (`RecentLogBuffer`/`IRecentLogBuffer`/`RecentLogEntry`) | **`Library`** | **belongs-in-shared** | S | High — in-memory Serilog sink + read API for operator dashboards |
| 13 | **HTTP resilience window helper** (`HttpResilienceConfigurator` + `HttpResilienceOptions`) | **`ServiceDefaults`** | **belongs-in-shared** | S | Medium — derives valid resilience windows from attempt timeout |
| — | `AnthropicAgentRunner` + `IAgentRunner` + `AgentModels` | stays-in-DeveloperAgent | **skip (policy)** | — | Welded to `ProjectItem`/`TaskWorkspace`/`PullRequest` |
| — | `ReviewerAgent` + `SubmitReviewTool` + `ReviewResult` | stays-in-DeveloperAgent | **skip (policy)** | — | Drives `IGitHubProjectService` + persona §9 |
| — | `CommentOnItemTool` + `CreatePullRequestTool` | stays-in-DeveloperAgent | **skip (policy)** | — | Every edge is a GitHub policy type |
| — | Actor + `ITaskStateStore` claim/write-through | stays-in-DeveloperAgent | **blocked (TState rewrite)** | L | `ProgrammingTaskState` saturated with `TaskPhase`/`ApprovalStatus` |
| — | `AgentMetrics` | stays-in-DeveloperAgent | **skip (semantic policy)** | — | `time_to_pr`/`build_test.pass_rate` encode the lifecycle |
| — | `OperatorCommandService` / `OperatorCommand` | stays-in-DeveloperAgent | **skip (policy)** | — | Reads policy `TaskState` |
| — | `SecretsBundle` + `SecretsBundleGitHubTokenProvider` + `HttpClientNames` | stays-in-DeveloperAgent | **skip (policy shell)** | — | The host-specific aggregate the resolver feeds |

**Three challenge corrections to note:**

- **Cluster D (`Agent.Tools`):** survey said *blocked, effort L, gated entirely on the sandbox*. **Corrected to "ready-with-seam-refactor"** — only `ShellRunTool` touches the sandbox command types; the file-tool core does not, and bundling them hid both the readiness and a circular-dependency trap.
- **`ShellRunTool` placement:** **corrected** — it belongs *with* `Agent.Sandbox`, not in `Agent.Tools`. Keeping it in `Tools` while `Tools` owns `IPathDenyPolicy` creates an `Agent.Tools ↔ Agent.Sandbox` cycle.
- **`BranchName` home:** survey said `Library`; challenge flagged that `Library` drags Azure Monitor + Serilog + Hosting into every reuser, so **co-locate in `Agent.Workspace`** (pure-BCL) instead.

---

## 3. Dependency DAG & extraction order

The only hard structural edges are around the sandbox. Everything else is an independent DAG node.

```text
Independent (extract any time, parallelizable):
  Library:         ISecretResolver, RecentLogBuffer
  ServiceDefaults: HttpResilienceConfigurator
  Agent.Mcp · Agent.Runtime · Agent.Workflow      (zero cross-edges)

The sandbox spine (order matters):

   Agent.Tools  ──owns──►  IPathDenyPolicy (host seam interface)
       ▲                          │
       │ Sandbox depends on Tools │ for the seam
       └───────── Agent.Sandbox  (CommandSandbox, deny policies, ShellRunTool,
                       owns CommandResult + TaskWorkspace)
                              ▲
                              │ Workspace depends on Sandbox (ICommandSandbox + CommandResult)
                       Agent.Workspace  (GitClient, WorkspaceManager, BranchName,
                                          DiffScopeLimitOptions)
```

### Cross-candidate edges (explicit)

- **`Agent.Workspace` → `Agent.Sandbox`**: `GitClient` shells every git verb through `ICommandSandbox.RunAsync` and parses `CommandResult`. Workspace cannot compile in isolation until Sandbox exists. → **Sandbox before Workspace.**
- **`Agent.Tools` (file core) → `Agent.Sandbox`**: the acyclic resolution is **`Agent.Tools` owns `IPathDenyPolicy`** as a seam interface, and **`Agent.Sandbox` depends on `Agent.Tools`** for it. The reverse (Sandbox owns it, Tools re-declares) plus keeping `ShellRunTool` in Tools is the **cycle** the challenge flagged — avoid it.
- **Shared currency records (cycle-avoidance):** `CommandResult` (produced by Sandbox, consumed by Workspace's `GitClient`) and `TaskWorkspace` (consumed by `PathValidator` and by Workspace) must each have **one owner**. Recommended: both live in `Agent.Sandbox`; `Agent.Workspace` references Sandbox for them. This keeps the DAG a clean line `Tools → Sandbox → Workspace` with no back-edges.
- **`Agent.Workspace` GitHub-token edge:** replace `SecretsBundle.GitHubToken` with the existing `Agent.GitHub.IGitHubTokenProvider` seam — do **not** take a `ProjectReference` on `Agent.GitHub` just for the clone URL (it would drag Octokit in); pass `repoUrl` as data (it already is a `CloneAsync` parameter).
- **`Agent.Mcp` / `Agent.Runtime` / `Agent.Workflow`:** zero cross-candidate edges — extract in any order, no prerequisites.

---

## 4. Top ready candidates — full split

Each mirrors the `Agent.GitHub`/`Agent.Memory` pattern: **generic core moves**, **config records move with the lib as public surface**, **policy stays as the host shell**, **host fills seams via interfaces + an `Add*Services` DI extension + an `Action<IHttpClientBuilder>` egress/resilience callback.**

### 4.1 `Agent.Sandbox` — highest value, extract first

**Generic core (moves):** `ICommandSandbox` + `CommandSandbox`; `IProcessRunner` + `DefaultProcessRunner` (internal); `ICommandDenyPolicy` + `CommandDenyPolicy`; `IPathDenyPolicy` *(consumed from `Agent.Tools` — see DAG)* + `PathDenyPolicy`; `HostAllowlistHandler`; `IContainerRuntime` + `DockerContainerRuntime`; `SandboxViolationException`; `CommandResult` (split out of `WorkspaceModels.cs`); and the config records `SandboxOptions` + `CommandDenyRule`, `WorkspaceOptions` (RootPath + AllowedCommands), `ContainerRuntimeOptions`. Add a lib-side `AddSandboxServices(...)` DI extension carrying the internal-ctor factory wiring currently inlined in `Program.cs`.

**Policy shell (stays):** `PathValidator.ResolveOrThrow` (coupled to `TaskWorkspace`); the `SandboxViolationException → AgentRunOutcome.SandboxViolation` lifecycle mapping; the canonical appsettings deny/allow/host **values** + the `ValidateOnStart` fail-closed checks.

**Host seams:** an `Action<IHttpClientBuilder>` (host composes `HostAllowlistHandler` + `AddStandardResilienceHandler` onto its named clients — `Microsoft.Extensions.Http.Resilience` stays host-side, identical to `AddGitHubProjectServices`); host supplies the appsettings config values + the `ValidateOnStart` policy; `IPathDenyPolicy` interface is owned by `Agent.Tools`.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Http" Version="10.0.8" />
    <PackageReference Include="Microsoft.Extensions.Options" Version="10.0.8" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.8" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.8" />
  </ItemGroup>
  <ItemGroup>
    <!-- owns IPathDenyPolicy; see DAG — keeps the line Tools→Sandbox→Workspace acyclic -->
    <ProjectReference Include="..\Agent.Tools\Agent.Tools.csproj" />
  </ItemGroup>
</Project>
```

Add `[InternalsVisibleTo("Agent.Sandbox.Tests")]` (mirrors `Agent.GitHub`).

**Key risks:** security-critical — the extraction must preserve **exact** behaviour: deny-runs-before-allow; fail-closed compound-segment validation (a denied segment rejects the whole line before any segment runs); `-c` auth-pair stripping; OS-specific path-deny casing/glob compilation; and the exact `SandboxViolationException` message contract (`'cwd outside workspace root'` / `'not in AllowedHosts'`) that lifecycle code and tests match on. Move the existing `CommandSandboxTests`/`PathDenyPolicy`/`HostAllowlistHandler` tests with the lib and keep them green. Two host-side follow-ons: (1) split `WorkspaceModels.cs` so `CommandResult` moves while `TaskWorkspace`/`DiffStats` stay; (2) replace internal-ctor DI factory lambdas in `Program.cs` with `AddSandboxServices(...)`. **Test-migration scope understated by the survey:** host tests for *staying* types (`GitClientTests` does `new DefaultProcessRunner()`, plus the container-isolation integration tests) reach sandbox internals via `DeveloperAgent.Tests`' `InternalsVisibleTo`; rewire them through the public `AddSandboxServices` path or they stop compiling.

### 4.2 `Agent.Mcp` — cleanest near-total lift

**Generic core (moves):** `IMcpToolSource` + `McpToolSource`, `IMcpClientConnector` + `StdioMcpClientConnector`, `McpOptions` + `McpServerOptions` (move as public config surface), and a new `AddMcpServices(...)` DI extension owning the `AddOptions<McpOptions>().Bind("McpServers")` + `AddSingleton` wiring now inline in `Program.cs`.

**Policy shell (stays):** essentially code-free — only the appsettings `McpServers` *data* (which servers exist) and the `AnthropicAgentRunner` consumer, which touches only the generic `IMcpToolSource → IReadOnlyList<AITool>` contract.

**Host seams:** none required today. (`Env` flows to the child process verbatim; an optional future `Func<string,string>` env-value resolver is **not** manufactured now.)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ModelContextProtocol" Version="1.3.0" />
    <PackageReference Include="Microsoft.Extensions.AI.Abstractions" Version="10.5.2" />
    <PackageReference Include="Microsoft.Extensions.Options" Version="10.0.8" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.8" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.8" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Abstractions" Version="10.0.8" />
  </ItemGroup>
</Project>
```

Add `[InternalsVisibleTo("Agent.Mcp.Tests")]`.

**Key risks:** two mechanical refactors, neither a blocker: (1) **de-hardcode** `"GitHub"`/`"Context7"` — restructure `McpOptions` from two fixed properties to a `Dictionary<string,McpServerOptions> Servers` map and iterate it (churns appsettings shape + three test files: `McpOptionsTests`, `McpOptionsBindingTests`, `McpToolSourceTests`); (2) add `AddMcpServices` and remove inline `Program.cs` registration. `McpOptionsBindingTests` reads live appsettings via `ProductionSandboxConfig`, so either keep that one in `DeveloperAgent.Tests` or relocate the fixture.

### 4.3 `Agent.Runtime` — factory + cap decorator + persona loader

**Generic core (moves):** `IAgentChatClientFactory` + `AnthropicChatClientFactory` (named-client const becomes public `AnthropicHttpClients.ChatClient`; egress/resilience via host `Action<IHttpClientBuilder>`); `TurnCountingChatClient` + `HardCapReachedException` + `ToolCallLimitReachedException` (decorator over `IChatClient`; **counter passed as a small mutable DTO, e.g. `RunCounters { int TurnsUsed }`, plus an int cap — not `AgentRunState`**); `PersonaLoader` **string-ctor only**.

**Policy shell (stays — corrected from survey's "genericCore"):** `ReviewerPersonaLoader` stays (its ctor consumes the policy `ReviewerOptions`; only the underlying `PersonaLoader` string-ctor is generic); `AnthropicOptions` stays (the `IAnthropicApiKeyProvider` seam supersedes the `ApiKeySecretName` indirection); the `PersonaLoader(IOptions<AgentOptions>)` convenience ctor stays host-side as a thin DI wrapper.

**Host seams:** `IAnthropicApiKeyProvider` (NEW — mirrors `IGitHubTokenProvider`; host implements via `SecretsBundle`); `Action<IHttpClientBuilder>` for the `anthropic` named client; `IHostEnvironment` for `PersonaLoader` content-root resolution.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Agents.AI.Anthropic" Version="1.1.0-rc1" />
    <PackageReference Include="Anthropic" Version="12.24.1" />
    <PackageReference Include="Microsoft.Extensions.AI.Abstractions" Version="10.5.2" />
    <PackageReference Include="Microsoft.Extensions.Http" Version="10.0.8" />
    <PackageReference Include="Microsoft.Extensions.Options" Version="10.0.8" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.8" />
  </ItemGroup>
</Project>
```

Make `TurnCountingChatClient`/`HardCapReachedException` public on move (the host's `AnthropicAgentRunner` does `new TurnCountingChatClient(...)`).

**Key risks:** keep the `Anthropic 12.24.1` floor (the `WebSearchToolResultContent Results→Outputs` Abstractions-10.5.x compat reason) or a `MissingMethodException` returns. The cap decorator **must** swap `AgentRunState` for a counter DTO or it slips to blocked (it would otherwise pull in `PullRequest`/`SandboxViolation` policy). Getting the egress/resilience composition wrong silently bypasses `HostAllowlistHandler`. The `AnthropicAgentRunner` run-loop and `ReviewerAgent` are **policy and stay** — `Agent.Runtime` is only the transport/cap/persona pieces.

### 4.4 `Agent.Workflow` — Dapr workflow-instance inspector  *(decision: kept as a separate assembly — see §4.4a)*

**Generic core (moves):** `IWorkflowInstanceInspector` + `WorkflowInstanceDisposition` enum + `DaprWorkflowInstanceInspector` (maps the un-constructible `WorkflowState` to a plain disposition enum — the value is the *mockable seam* around a type with no public ctor) + a public `AddWorkflowInspector(IServiceCollection)` extension.

**Policy shell (stays):** the **entire** `DeveloperTaskWorkflow` orchestration body; **every** activity (`AcquireTask`/`CreateBranch`/`Plan`/`ModifyCode`/`Build`/`Test`/`CreatePullRequest`/`WaitForReview`/`Done`/`CompactMemory` + the `Load`/`Save`/`DeleteAgentSession` activities); the `AddDeveloperTaskWorkflow()` registration extension that calls `RegisterWorkflow`/`RegisterActivity`; and the active→skip / terminal→purge / notfound→schedule **decision** in `AgentLifecycleService`.

**Host seams:** none — takes only the generic Dapr `IDaprWorkflowClient` the host already registers.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Dapr.Workflow" Version="1.17.9" />
  </ItemGroup>
</Project>
```

Add `[InternalsVisibleTo("Agent.Workflow.Tests")]`.

**Key risks:** `DaprWorkflowInstanceInspector` is `internal sealed` and its `AddSingleton` registration sits *inside* the policy-saturated `AddDeveloperTaskWorkflow()` extension — you cannot literally relocate that line. Per the repo's own precedent: keep the concrete internal, ship the public `AddWorkflowInspector(IServiceCollection)` extension in the new lib, and have the host call it. The public interface + enum are already public and already mocked by tests.

#### 4.4a Decision — `Agent.Workflow` is a standalone assembly

This candidate is the smallest reusable unit in the roadmap (two files, ~80 lines), and one could argue for folding the inspector into a shared project or deferring it until a second Dapr-workflow agent exists. **That argument is explicitly overridden: `Agent.Workflow` ships as its own assembly.** Rationale:

- Neither `Library` nor `ServiceDefaults` carries the `Dapr.Workflow` package, and adding it there would push a workflow-engine dependency onto every consumer of those shared projects. A dedicated assembly keeps that dependency opt-in.
- It establishes the home for *future* agent-neutral Dapr-workflow mechanics (e.g. a generic idempotent-scheduling helper, instance-id conventions) so they are not later retrofitted into the wrong project.
- Reuse model is unchanged by this decision: another agent references `Dapr.Workflow` (the actual engine) **plus** `Agent.Workflow` (the inspector), and supplies its **own** `Workflow<TInput,TOutput>` subclass, its **own** activities, and its **own** `AddXxxWorkflow()` registration. `Agent.Workflow` contributes only the testable "is this instance id free/active/terminal?" probe.

### 4.5 `Agent.Tools` (file-tool core) — ready-with-seam-refactor (corrected)

**Generic core (moves):** `ITool`, `ToolResult` + `ToolErrorKind`, `PathValidator` (workspace-boundary + deny gate), `ReadFileTool`/`WriteFileTool`/`EditFileTool`/`ListDirectoryTool`, `MafToolAdapter` (`ITool → AIFunction` bridge), and the **`IPathDenyPolicy` seam interface (owned here).**

**Policy shell (stays):** `CommentOnItemTool`, `CreatePullRequestTool` (persona §9 body + `MaxPRSize` gate + `ProjectItem.ContentNodeId`); `ProjectItem`/`ProjectState`; `AgentRunState`; the `AnthropicAgentRunner` composition wiring.

**Host seams:** `IToolContext` (slim — exposes only `WorkspaceRoot`, replacing the `ToolContext.Item`/`AgentRunState` policy fields the file tools never touch); `IToolCallBudget` (or an `Action` callback — `MafToolAdapter`'s counter-increment + `MaxToolCalls` gate, replacing direct `AgentRunState` mutation); `IPathDenyPolicy` (owned here, implemented by `Agent.Sandbox`).

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <!-- AIFunction/AIFunctionArguments/AITool; System.Text.Json is in the net10 shared framework -->
    <PackageReference Include="Microsoft.Extensions.AI.Abstractions" Version="10.5.2" />
  </ItemGroup>
  <!-- NO ProjectReference to Agent.Sandbox. This lib OWNS IPathDenyPolicy; Sandbox depends on IT. -->
</Project>
```

**Key risks:** **do not bundle `ShellRunTool` here** — it is the only file that imports `ICommandSandbox`/`CommandResult`/`SandboxViolationException`, and keeping it here while owning `IPathDenyPolicy` creates the `Agent.Tools ↔ Agent.Sandbox` cycle. Slim `ToolContext` to a `WorkspaceRoot` seam (a naive lift drags `ProjectItem`/`AgentRunState` policy in). The `MafToolAdapter` budget refactor touches a tested termination path (`FunctionInvokingChatClient MaximumConsecutiveErrorsPerRequest=0`) — regressions silently break run-termination. `MafToolAdapter` is `internal` and lives in `Agent/` not `Tools/` — internal→public + a file move are routine.

### 4.6 `Agent.Workspace` — blocked until Sandbox + 2 config splits

**Prerequisites (must land first):**

1. **`Agent.Sandbox` extracted** (provides `ICommandSandbox` + `CommandResult`).
2. **Split `ScopeLimitOptions`** — carve out a tiny `DiffScopeLimitOptions { MaxChangedFiles; MaxChangedLines }` that moves with the lib; the host keeps the big `ScopeLimitOptions` (`MaxModelTurns`/`MaxToolCalls`/`MaxExecutionTimeSeconds`/`MaxRetryCount`/`MaxPR*`) and binds both from the one `ScopeLimits` section.
3. **Split `WorkspaceOptions`** *(challenge-added blocker)* — `RootPath` is shared by **both** `CommandSandbox` (cluster A) and `WorkspaceManager` (cluster B); `AllowedCommands` is sandbox-only. Same split treatment as `ScopeLimitOptions`. Mind the documented `ConfigurationBinder` double-append gotcha when the host binds both halves from a single section.

**Generic core (moves, after prereqs):** `IGitClient`/`GitClient` (swap `SecretsBundle`→`IGitHubTokenProvider`; drop the **dead** `WorkspaceOptions` field `GitClient` injects-but-never-reads; take `DiffScopeLimitOptions`; take `repoUrl` by param); `IWorkspaceManager`/`WorkspaceManager`; `BranchName`; `ScopeLimitExceededException` (+ `ScopeLimit` enum trimmed to the two diff members); `TaskWorkspace`/`DiffStats` *(owned by Sandbox per the cycle-avoidance rule — Workspace references them)*; an `AddWorkspaceGitServices(...)` extension.

**Policy shell (stays):** `CreateBranchActivity`, `ModifyCodeActivity`/`PlanActivity`/`DoneActivity`/`AcquireTaskActivity`, `CreatePullRequestTool`, `PullRequestBodyBuilder`, `TaskExecutor`, `SecretsBundle`/`SecretsBundleGitHubTokenProvider`, residual `ScopeLimitOptions`.

**Host seams:** `IGitHubTokenProvider` (reuse existing); `ICommandSandbox` + `CommandResult` (from `Agent.Sandbox`).

**Key risks:** the A↔B circular-dependency trap (give `TaskWorkspace` and `CommandResult` one owner each — recommend both in `Agent.Sandbox`); the `http.extraheader` Basic-auth token handling and the default-branch push-guard are subtle — keep their tests; confirm `BranchName`'s `agent/` prefix is a configurable default, not hard policy.

---

## 5. New library vs. existing shared project

Not every generic thing earns a new assembly. These three are **belongs-in-shared** moves with **no `.csproj` change** (the target already carries the package):

| Utility | Target | Why existing project (not new lib) |
|---------|--------|-----------------------------------|
| `ISecretResolver` + `EnvAndUserSecretsResolver` | **`Library`** (`Library.Secrets`) | `Library.csproj` already references `Microsoft.Extensions.Configuration.Abstractions 10.0.8`; resolver needs only that + BCL `Environment`. `SecretsBundle` stays in DeveloperAgent and consumes it (inbound edge, non-blocking). |
| `RecentLogBuffer` + `IRecentLogBuffer` + `RecentLogEntry` | **`Library`** (`Library.Logging`) | `Library.csproj` already references `Serilog 4.3.1` (covers `ILogEventSink` + `LogEvent`/`LogEventLevel`); `System.Threading.Lock` is net10 BCL. Zero DeveloperAgent edges. |
| `HttpResilienceConfigurator` + `HttpResilienceOptions` | **`ServiceDefaults`** | `ServiceDefaults.csproj` already references `Microsoft.Extensions.Http.Resilience` and *owns* the resilient-HTTP concern. `Library` would have to **add** the package — so `ServiceDefaults` is the correct home. |

**Migration mechanics (all minor):** the moves are namespace-only, but host **test** projects re-point `using`s — `SecretResolverTests`, `RecentLogBufferTests`/`DashboardComponentTests`, `HttpResilienceConfiguratorTests`, plus Razor `@using DeveloperAgent.Dashboard` in `RecentLogPanel.razor`/`Dashboard.razor`/`OperatorActions.razor`. No behaviour change.

**Counter-recommendation:** put **`BranchName` in `Agent.Workspace`**, *not* `Library` — although it compiles in `Library` unchanged (pure BCL), `Library` drags Azure Monitor + Serilog + Hosting into every reuser, contradicting the agent-neutral goal.

---

## 6. Leave as policy (stays in DeveloperAgent)

These are the **policy shell** — the analogues of `GitHubProjectService`/`ProjectState` that the GitHub split deliberately kept. Extracting them would re-couple a "neutral" lib to the developer lifecycle.

- **`AnthropicAgentRunner` + `IAgentRunner` + `AgentModels`** — `AgentRunRequest` carries `ProjectItem` + `TaskWorkspace`; `AgentRunResult`/`AgentRunState` carry `PullRequest`; `AgentRunOutcome` enumerates `SandboxViolation`; `BuildKickoffMessage` hardcodes the GitHub-item/branch/prior-reviewer-feedback prompt template. Welded to the lifecycle. (A generic "drive a MAF loop with caps + structured outcomes" core is a *downstream* candidate only after the tool abstraction + a generic run-request shape exist — not now.)
- **`ReviewerAgent` + `IReviewerAgent` + `ReviewResult` + `SubmitReviewTool`** — drives `IGitHubProjectService.GetPullRequestForReviewAsync`/`SubmitReviewAsync`, checks persona §9 via `PullRequestBodyBuilder.RequiredSectionHeaders`, typed on `ReviewVerdict`/`PullRequestReviewContext`. The canonical *consumer* of the extracted libs, not a generic unit.
- **`CommentOnItemTool` + `CreatePullRequestTool`** — every edge is a GitHub policy type (`ProjectItem.ContentNodeId`, §9 body, `MaxPRSize`/`ScopeLimitOptions`, `IGitClient.GetDiffStatsAsync`).
- **`ProgrammingTaskActor` + `IProgrammingTaskActor` + `ProgrammingTaskState` + `DaprActorTaskStateStore`/`ITaskStateStore`** — **blocked, not skip.** A real latent pattern (single-owner-per-item claim + write-through cache over durable Dapr-actor state) exists, but `ProgrammingTaskState` is saturated with `TaskPhase`/`ApprovalStatus` and the actor methods are PR-review-specific (`MarkApprovedAsync`/`SavePullRequestAsync`/…). Extraction is a **genericize-to-`TState` rewrite (effort L)**, not a lift. Revisit only when a second agent needs the claim pattern; then extract a generic `IItemClaimActor<TState>` first.
- **`AgentMetrics`** — the *literal* outward-edge test passes (only `System.Diagnostics.Metrics` BCL), but `agent.time_to_pr` and `agent.build_test.pass_rate` **semantically encode** the Plan/Build/Test/PR lifecycle (confirmed by `TaskExecutor.RecordTimeToPullRequest` at PR-open + `RecordTaskTerminated` terminal branches). This is the "ready hides coupling" trap — **do not extract.**
- **`OperatorCommandService` / `OperatorCommand` / `OperatorCommandResult`** — reads policy `TaskState` via `ITaskStateStore.Current`; `OperatorCommand` is in the explicit policy-exclusion set.
- **`SecretsBundle` + `SecretsBundleGitHubTokenProvider` + `HttpClientNames`** — the host-specific Anthropic+GitHub aggregate (policy by content), the host adapter to `IGitHubTokenProvider`, and the lone remaining host-specific `"anthropic"` const. The policy shell that makes the generic extractions clean.
- **`TaskExecutor` / `DeveloperTaskWorkflow` / all `Workflow/Activities/*` / `FailureCommentFormatter` / `AgentLifecycleService`** — the Plan/Build/Test/PR/Review state machine itself. `FailureCommentFormatter` switches on `AgentRunOutcome` (`HardCapReached`/`SandboxViolation`/`ApiError`) — pure run-outcome policy.

---

## 7. Recommended extraction sequence (Step-N, shippable slices)

Each step is a self-contained, test-green, mergeable slice matching the repo flow (branch → In-progress → tests → merge → Done).

**Wave 1 — zero-prerequisite, parallelizable (independent DAG nodes):**

- **Step-A:** move secret resolver into `Library` (`ISecretResolver` + `EnvAndUserSecretsResolver` → `Library.Secrets`; re-point `SecretsBundle` factory + test `using`s). *(S, belongs-in-shared.)*
- **Step-B:** move recent-log buffer into `Library` (`RecentLogBuffer` trio → `Library.Logging`; re-point `Program.cs` dual-role registration + Razor `@using` + tests). *(S.)*
- **Step-C:** move HTTP-resilience helper into `ServiceDefaults` (`HttpResilienceConfigurator` + `HttpResilienceOptions`; re-point `Program.cs` wiring + tests). *(S.)*
- **Step-D:** extract `Agent.Mcp` (de-hardcode server names to a `Servers` dictionary, add `AddMcpServices`, move tests to `Agent.Mcp.Tests`, add `InternalsVisibleTo`). *(S.)*
- **Step-E:** extract `Agent.Workflow` (Dapr inspector) with a public `AddWorkflowInspector` extension. **Standalone assembly per the §4.4a decision.** *(S.)*
- **Step-F:** extract `Agent.Runtime` (factory + `TurnCountingChatClient` cap decorator + `PersonaLoader` string-ctor). Introduce `IAnthropicApiKeyProvider` + `RunCounters` DTO + the `Action<IHttpClientBuilder>` seam. Keep `ReviewerPersonaLoader`/`AnthropicOptions` host-side. *(M.)*

**Wave 2 — the sandbox spine (strict order):**

- **Step-G:** extract `Agent.Tools` file-tool core. Publish `IPathDenyPolicy`; slim `ToolContext` → `IToolContext{WorkspaceRoot}`; introduce `IToolCallBudget`; move `ITool`/`ToolResult`/`PathValidator`/Read/Write/Edit/ListDir/`MafToolAdapter`. **Leave `ShellRunTool` behind for now.** *(M.)*
- **Step-H:** extract `Agent.Sandbox`. Split `WorkspaceModels.cs` (move `CommandResult`/`TaskWorkspace`); move command/path-deny, `HostAllowlistHandler`, `DockerContainerRuntime`, `SandboxViolationException`, **and `ShellRunTool`**; reference `Agent.Tools` for `IPathDenyPolicy`; add `AddSandboxServices` + the `Action<IHttpClientBuilder>` seam; rewire host internal-construction tests through the public path. *(M.)*

**Wave 3 — depends on Wave 2:**

- **Step-I:** split config records — carve `DiffScopeLimitOptions` out of `ScopeLimitOptions` and the workspace `RootPath`/`RepoUrl` record out of `WorkspaceOptions`; host binds both halves from the single sections (mind the double-append gotcha). *(S — prerequisite for Step-J.)*
- **Step-J:** extract `Agent.Workspace`. Move `GitClient`/`WorkspaceManager`/`BranchName`/`ScopeLimitExceededException`; swap `SecretsBundle`→`IGitHubTokenProvider`; drop the dead `WorkspaceOptions` field; take `repoUrl` by param; reference `Agent.Sandbox` for `ICommandSandbox`/`CommandResult`/`TaskWorkspace`; add `AddWorkspaceGitServices`. *(M.)*

**Explicitly deferred / not scheduled:** the `ProgrammingTaskActor` + `ITaskStateStore` claim pattern (blocked — `TState` rewrite, revisit on second consumer); a generic agent run-loop core (downstream of Steps G–J). `AgentMetrics`, `OperatorCommandService`, `SecretsBundle`, `AnthropicAgentRunner`, `ReviewerAgent`, and the workflow/lifecycle activities **stay as policy**.

---

**Relevant paths:** new assemblies live under `src/Agent.Sandbox/`, `src/Agent.Mcp/`, `src/Agent.Runtime/`, `src/Agent.Workflow/`, `src/Agent.Tools/`, `src/Agent.Workspace/` (mirroring `src/Agent.GitHub/` + `src/Agent.Memory/`); shared-project targets are `src/Library/Library.csproj` and `src/ServiceDefaults/ServiceDefaults.csproj`; the solution to add them to is `src/ClaudeAgentsSolo.slnx`.
