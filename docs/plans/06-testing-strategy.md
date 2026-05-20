# 06 — Testing Strategy

**Status:** draft — 2026-05-20
**Depends on:** plans `01`–`05` (each plan's `§Verification` is the menu of cases this plan owns the *infrastructure* for).

## Purpose

Make `tests/` non-empty and make `dotnet test src/ClaudeAgentsSolo.slnx` a meaningful signal. Phase 1 tests are pragmatic: a thick layer of unit tests covering business logic and contract boundaries, a thinner layer of opt-in integration tests gated by environment variables so CI without secrets stays green.

The single most-important phase-1 test is the `TaskExecutor` state-machine suite — that's where the whole walking skeleton lives. The rest exists to keep the state-machine tests honest by ensuring the pieces it composes also work.

## Deliverables

| Path | Change |
| ---- | ------ |
| `tests/DeveloperAgent.Tests/DeveloperAgent.Tests.csproj` | New xUnit project targeting `net10.0`. References `DeveloperAgent`, packages: `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `FluentAssertions`, `NSubstitute`. |
| `src/ClaudeAgentsSolo.slnx` | Add `<Project Path="../tests/DeveloperAgent.Tests/DeveloperAgent.Tests.csproj" />`. |
| `tests/DeveloperAgent.Tests/Configuration/` | Tests for plan 01 (options binding, validation). |
| `tests/DeveloperAgent.Tests/GitHub/` | Tests for plan 02 (PR body builder, project-state mapping with a fake REST/GraphQL transport). |
| `tests/DeveloperAgent.Tests/Workspace/` | Tests for plan 03 (`BranchName`, `CommandSandbox` allowlist + cwd check, `GitClient` against a temp repo). |
| `tests/DeveloperAgent.Tests/Agent/` | Tests for plan 04 (tools' path validation, runner's loop with a fake Anthropic client). |
| `tests/DeveloperAgent.Tests/Lifecycle/` | Tests for plan 05 (state machine with fakes for every external dependency). |
| `tests/DeveloperAgent.Tests/Fakes/InMemoryGitHubProjectService.cs` | A working `IGitHubProjectService` backed by a mutable dictionary. Used by lifecycle and agent tests; not a mock, a real test double. |
| `tests/DeveloperAgent.Tests/Fakes/FakeAnthropicClient.cs` | Scripted-response fake the `AnthropicAgentRunner` talks to in unit tests. |
| `tests/DeveloperAgent.Tests/Fakes/InMemoryFileSystem.cs` | *Optional.* If file-tool tests start to feel slow against the real disk, replace with this; otherwise use a `[Fixture]` temp directory per test. |
| `src/DeveloperAgent/Properties/InternalsVisibleTo.cs` | `[assembly: InternalsVisibleTo("DeveloperAgent.Tests")]` so the tests can exercise `internal` types without going public. |

## Test taxonomy

### Unit (default — always run)

Fast, deterministic, no I/O outside `Path.GetTempPath()`, no network. Make up >90 % of the suite.

**Configuration (plan 01)**
- `AgentOptions` defaults match the documented schema.
- A missing `Anthropic.ApiKeySecretName` fails validation at bind time.
- `WorkspaceOptions.AllowedCommands` defaults match the LLD list.

**GitHub (plan 02)**
- `PullRequestBodyBuilder`: every heading present, in order, blanks become `None`, empty `Summary` throws.
- `GitHubProjectService` against a fake `IGraphQLTransport` + a fake `IRestTransport`: state-name → option-ID caching is correct, `MoveItemAsync` is a no-op when current state equals target, `CreatePullRequestAsync` on `422 already_exists` returns the existing PR, `GetPullRequestStatusAsync` correctly combines pr + reviews + check-runs into the `PullRequestStatus` record.

**Workspace (plan 03)**
- `BranchName.ForTask`: 8-char thread-id path, slug path, ≤ 40 chars, deterministic hash, sanitisation of punctuation, empty-input fallback (throws).
- `CommandSandbox`: allowlist match by whole tokens, allowlist miss throws `SandboxViolationException`, cwd outside `WorkspaceOptions.RootPath` throws, captures + timeout via a fake `IProcessRunner`.
- `GitClient` against a temp on-disk repo (xUnit `IClassFixture` creating a bare upstream + a clone): `CheckoutNewBranchAsync` puts HEAD on the new branch, `PushAsync` refuses when HEAD is on `DefaultBranch` (assert without ever needing a real remote — use a local file-system "remote").

**Agent (plan 04)**
- Each tool's path validator rejects `..`, absolute paths outside the workspace, and symlink escapes if running on Linux.
- `EditFileTool` rejects non-unique `old_string` and missing `old_string`.
- `AnthropicAgentRunner` with `FakeAnthropicClient`:
  - tool_use → tool_result loop completes when the fake returns an assistant text-only response.
  - 41 consecutive tool calls → `HardCapReached`, counters correct.
  - Tool raising `SandboxViolationException` → `SandboxViolation`, no further fake calls made.
  - Cancellation token cancels mid-loop → `Cancelled`, partial history preserved.

**Lifecycle (plan 05)** — the *core* phase-1 test
- Happy path: one Ready item → InProgress → agent succeeds + returns PR → InReview → review fake reports Merged+Approved+ChecksGreen → Done. Assert exact GitHub-fake transition history.
- ChangesRequested: review fake returns ChangesRequested with feedback; agent fake records that `RunAsync` was called a second time with `PriorReviewFeedback` non-null and pointing at the same PR; second pass succeeds.
- Agent returns `Completed, PullRequest=null` → item released to Ready, comment posted.
- Agent returns `HardCapReached` → released to Ready with explanatory comment.
- `SandboxViolation` → released to Ready, comment posted *without* the offending command.
- `GetInFlightItemsAsync` returns one item at startup → loop logs and skips, then proceeds normally on the next tick.
- Cancellation mid-review-wait → propagates, no spurious state transitions after cancellation.

### Integration (opt-in — env-gated)

Run by setting an env var; absent → tests are *skipped*, not failed. Use xUnit's `Skip = "…"` from a `[FactWhenEnv]` helper:

| Env var | What it unlocks |
| ------- | ---------------- |
| `GITHUB_INTEGRATION_REPO` + `GITHUB_INTEGRATION_TOKEN` + `GITHUB_INTEGRATION_PROJECT` | Plan 02's real GraphQL+REST cycle against a throwaway repo + project. Creates a draft issue (or pre-existing fixture item), walks the state cycle, cleans up. |
| `GIT_INTEGRATION_REPO` | Plan 03's `GitClient` against a real `git` binary cloning a public sandbox repo into a temp dir. Asserts clone, branch, commit, refusing to push to default. |
| `ANTHROPIC_INTEGRATION_KEY` | Plan 04's smoke test: trivial task ("add a comment line to README, then call create_pull_request") against a throwaway repo. Asserts the PR is opened with the four-section body. |

Integration tests live next to their unit tests but carry an explicit xUnit trait so they're easy to filter. **Convention:** every integration test (or its containing class) is decorated with `[Trait("Category", "Integration")]`. Unit tests carry no `Category` trait — that way the filter `Category!=Integration` matches them by default. Forgetting the trait silently demotes an integration test into the default-fast run, so include "trait present on every `[Fact(Skip = …)]`" in the test-review checklist.

```bash
dotnet test --filter "Category!=Integration"     # default-fast
dotnet test --filter "Category=Integration"      # full (requires env vars per the table above)
```

### End-to-end (manual)

The phase-1 acceptance scenario from `00-roadmap.md` §Phase-1 acceptance criteria is **not** an automated test in phase 1. It is a manual checklist the operator runs once before declaring the skeleton done. Automating it requires either a long-lived test project on GitHub or a self-hosted GitHub server, both of which are phase-2 ergonomics.

## Conventions

- **xUnit `Fact` / `Theory`**; no `[TestMethod]`.
- **FluentAssertions** for readable assertions (`result.Outcome.Should().Be(TaskOutcome.Done)`).
- **NSubstitute** for protocol-style mocks of single-method interfaces (Anthropic client, REST transport). Hand-rolled fakes for state-bearing services (the in-memory GitHub project, the in-memory file system) — those tend to grow beyond what a mock framework expresses cleanly.
- **No shared mutable state across tests.** Each test gets its own `InMemoryGitHubProjectService` instance, its own temp dir, its own fake Anthropic client.
- **No `Task.Delay`, no real `PeriodicTimer`** in tests — inject `TimeProvider` (.NET 8+) into `AgentLifecycleService` so tests can advance time deterministically. (Plan 05 is silent on this; treat the `TimeProvider` injection as a small contract change owned by plan 06.)
- **Cancellation tokens are passed everywhere** — tests assert that every interface respects them by triggering cancellation and observing prompt return.

## Out of scope (deferred to phase 2)

- **Property-based tests** (FsCheck) — useful for `BranchName` and the PR-body builder, but not required for phase 1.
- **Mutation testing** (Stryker) — phase 2 once the suite is stable.
- **Performance / load tests** — not relevant until concurrent items land.
- **Snapshot tests for PR bodies / comments** — diff-noise risk outweighs the value at phase 1; revisit if false negatives become a problem.
- **CI workflow files** in `.github/workflows/` — phase 1 documents the `dotnet test` commands; phase 2 adds the workflow YAML.
- **Test against Dapr / Redis** — phase 2 only.

## Verification

- `dotnet test src/ClaudeAgentsSolo.slnx --filter "Category!=Integration"` is green on a clean checkout with **no environment configuration** beyond a .NET 10 SDK.
- The `Lifecycle` suite includes all six scenarios listed under §Test taxonomy → Lifecycle, each as a named `Fact` (e.g. `Happy_path_walks_Ready_to_Done`).
- Each test file ≤ 300 lines; longer files split by scenario.
