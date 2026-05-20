# 05 — Lifecycle Loop

**Status:** draft — 2026-05-20
**Depends on:** `01`–`04`.
**Unblocks:** end-to-end demo, `06-testing-strategy.md`.

## Purpose

The orchestrator that turns the standalone services (GitHub, workspace, agent) into a continuously-running agent. One `IHostedService` polls the GitHub project, picks one `Ready` item at a time, drives it through the state machine to `Done`, then picks the next. Single-replica, sequential, in-memory.

The lifecycle loop owns *project state* (Ready/InProgress/InReview/Done) and the PR-approval *wait*. It does **not** own code edits — the agent core owns those. It does not own the PR-create call either — the agent calls the `create_pull_request` tool from inside its loop, which delegates back through this layer's services. The orchestrator's job is to set up the conditions for the agent to work, observe the outcome, and move state.

## Deliverables

| Path | Change |
| ---- | ------ |
| `src/DeveloperAgent/Lifecycle/AgentLifecycleService.cs` | `IHostedService` that owns the outer poll loop. |
| `src/DeveloperAgent/Lifecycle/TaskExecutor.cs` | Per-item state machine. Called once per task by the lifecycle service. |
| `src/DeveloperAgent/Lifecycle/TaskState.cs` | The phase enum + per-task DTO. |
| `src/DeveloperAgent/Lifecycle/InMemoryTaskStateStore.cs` | Tracks the active task (and only the active task) so logging/diagnostics can see "what is the agent doing right now". |
| `src/DeveloperAgent/Program.cs` | DI: `AddHostedService<AgentLifecycleService>()`, `AddSingleton<TaskExecutor>()`, `AddSingleton<ITaskStateStore, InMemoryTaskStateStore>()`. |

## Public surface

```csharp
namespace DeveloperAgent.Lifecycle;

public sealed class AgentLifecycleService : BackgroundService
{
    // Constructor takes ILogger, IOptions<AgentOptions>, IGitHubProjectService, TaskExecutor, ITaskStateStore.
    protected override Task ExecuteAsync(CancellationToken stoppingToken);
}

public sealed class TaskExecutor
{
    // Drives a single item from Ready → Done (or failure). Returns when terminal.
    Task<TaskOutcome> RunAsync(ProjectItem item, CancellationToken ct);
}

public enum TaskPhase
{
    Acquired,        // moved to In Progress, no workspace yet
    WorkspaceReady,  // cloned + branched
    AgentRunning,    // agent loop in flight
    PullRequestOpen, // PR created by agent's create_pull_request tool, item moved to In Review
    AwaitingReview,  // polling PR status
    Done,
    Failed
}

public enum TaskOutcome { Done, Failed, Cancelled }

public sealed record TaskState(
    string ProjectItemId,
    int IssueNumber,
    string Title,
    TaskPhase Phase,
    string? BranchName,
    int? PullRequestNumber,
    string? LastError,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? PullRequestOpenedAtUtc,    // cursor for the very first ChangesRequested fetch
    DateTimeOffset? LastReviewPolledAtUtc);    // advanced on every review-status tick; `sinceUtc` for GetReviewFeedbackSinceAsync

public interface ITaskStateStore
{
    TaskState? Current { get; }
    void Set(TaskState state);
    void Clear();
}
```

`ITaskStateStore` is intentionally minimal in phase 1: one slot, in memory. Phase 2's Dapr Actor implementation slots in behind the same interface (renamed if needed, but the lifecycle loop's call sites don't move).

## Behavior

### Outer poll loop (`AgentLifecycleService.ExecuteAsync`)

```text
on start:
  in_flight = await github.GetInFlightItemsAsync(ct)
  if in_flight.Any():
    log warn "items already in InProgress/InReview at startup; skipping in phase 1 (recovery is phase 2)"
    for each: log {item.Number, item.Title, item.State}

  using timer = new PeriodicTimer(AgentOptions.PollIntervalSeconds)
  while (await timer.WaitForNextTickAsync(stoppingToken)):
    item = await github.TryGetNextReadyItemAsync(stoppingToken)
    if (item is null): continue
    try:
      await taskExecutor.RunAsync(item, stoppingToken)
    catch OperationCanceledException when stoppingToken.IsCancellationRequested:
      throw
    catch Exception ex:
      log error "task {item.Id} failed unexpectedly"
      try await github.AddItemCommentAsync(item.Id, "Agent crashed: " + ex.Message)
      try await github.MoveItemAsync(item.Id, ProjectState.Ready)   # release the item back for next run
    finally:
      taskStateStore.Clear()
```

One Ready item per outer tick. The next item is picked on the *next* tick, not immediately after the current one finishes — this gives the operator a small breathing window to pause the loop by emptying `Ready` between items.

### Per-item state machine (`TaskExecutor.RunAsync`)

```text
1. ACQUIRE
   await github.MoveItemAsync(item.Id, InProgress)
   state.Phase = Acquired
   branchName = BranchName.ForTask(threadId: null, taskTitle: item.Title)

2. WORKSPACE
   ws = await workspaceManager.PrepareAsync(item.Id, branchName, ct)
   await gitClient.CloneAsync(ws, repoUrl, ct)
   await gitClient.CheckoutNewBranchAsync(ws, ct)
   state.Phase = WorkspaceReady, state.BranchName = branchName

3. AGENT, ROUND 1
   priorFeedback = null
   result = await agentRunner.RunAsync(new AgentRunRequest(item, ws, priorFeedback), ct)
   state.Phase = AgentRunning  (set just before the call so logs reflect it)

   if result.Outcome != Completed:
     await github.AddItemCommentAsync(item.Id, formatFailureComment(result))
     await github.MoveItemAsync(item.Id, Ready)        # release; do not block the queue
     return Failed

   if result.PullRequest is null:
     # Agent finished without opening a PR — count as failure.
     await github.AddItemCommentAsync(item.Id, "Agent finished without opening a PR — releasing.")
     await github.MoveItemAsync(item.Id, Ready)
     return Failed

   pr = result.PullRequest
   state.Phase = PullRequestOpen
   state.PullRequestNumber = pr.Number
   state.PullRequestOpenedAtUtc = TimeProvider.GetUtcNow()
   state.LastReviewPolledAtUtc  = state.PullRequestOpenedAtUtc   # first ChangesRequested fetch reads everything since the PR opened

4. MOVE TO IN REVIEW
   await github.MoveItemAsync(item.Id, InReview)
   state.Phase = AwaitingReview

5. REVIEW WAIT LOOP
   using reviewTimer = new PeriodicTimer(AgentOptions.ReviewPollIntervalSeconds)
   while (await reviewTimer.WaitForNextTickAsync(ct)):
     status = await github.GetPullRequestStatusAsync(pr.Number, ct)

     if status.Merged and status.ChecksGreen and status.Review == Approved:
       await github.MoveItemAsync(item.Id, Done)
       state.Phase = Done
       return Done

     if status.Review == ChangesRequested:
       # Pull the latest review/issue comments since the previous successful review poll
       # (or since the PR was opened on round 1). The cursor is advanced AFTER the fetch
       # so retries on a transient error don't lose comments.
       feedback = await github.GetReviewFeedbackSinceAsync(pr.Number, state.LastReviewPolledAtUtc.Value, ct)
       state.LastReviewPolledAtUtc = TimeProvider.GetUtcNow()
       await github.MoveItemAsync(item.Id, InProgress)   # back to active work

       result = await agentRunner.RunAsync(new AgentRunRequest(item, ws, priorReviewFeedback: feedback), ct)
       if result.Outcome != Completed:
         await github.AddItemCommentAsync(item.Id, formatFailureComment(result))
         await github.MoveItemAsync(item.Id, Ready)
         return Failed
       # Agent pushed more commits on the same branch (per persona §10); PR remains the same.
       # On round 2+ the agent's create_pull_request tool short-circuits to the existing PR (plan 02 §CreatePullRequestAsync).
       await github.MoveItemAsync(item.Id, InReview)
       continue

     # Pending or Approved-but-not-merged-yet → keep waiting.

6. CLEANUP
   await workspaceManager.ReleaseAsync(ws, ct)   # only on Done
```

`formatFailureComment(result)` produces a markdown blurb naming the outcome (`HardCapReached`, `SandboxViolation`, `ApiError`, etc.) and the `TerminationReason` from the agent. It never includes secrets, full stack traces, or the offending command line in the sandbox-violation case.

### Why release back to `Ready` on failure

Phase 1 has no retry policy and no per-item `LastError` history. Putting failed items back into `Ready` would cause infinite loops if the failure is deterministic. So failed items are released back to `Ready` **with a comment** explaining the failure; the operator decides whether to retry by leaving it in `Ready`, edit the issue, or move it elsewhere. This is acceptable for phase 1 because the operator is the only consumer; phase 2's actor will track retry counts and apply an exponential give-up policy.

### What happens on shutdown

`stoppingToken` cancels mid-task → `OperationCanceledException` propagates. The lifecycle service rethrows. The item is left in whatever GitHub state it was in (typically `In Progress`). The workspace dir is left on disk; the next clean start sees it in `GetInFlightItemsAsync`, logs a warning, and ignores it (phase 1 doesn't recover; the operator wipes the dir or moves the item back manually if they want).

### Logging surface

Every phase transition logs at `Information` with structured fields: `{ItemId, IssueNumber, Title, Phase, BranchName, PullRequestNumber, Round, ElapsedFromStart}`. Tool invocations are logged inside plan 03/04, not here. Failures log at `Error` with the same fields plus the failure reason.

The current `TaskState` is exposed via the `/info` endpoint (extended from the existing Library extension) so an operator can `curl http://localhost:8089/info` and see what the agent is doing. **Do not** expose secrets, full diffs, or command stdout via this endpoint — only the structured `TaskState` fields above.

## Cancellation contract

- `stoppingToken` flows into every external call. No timeouts via `Thread.Sleep`.
- The agent runner respects cancellation per plan 04. Tool invocations that respect cancellation propagate it.
- Per-round agent execution has no hard timeout in phase 1; the model's hard turn cap (`MaxModelTurnsHardCap`) is the only ceiling. Phase 2 wires per-task wall-clock + per-tool timeouts.

## Out of scope (deferred to phase 2)

- **Resume on startup** — phase 1 logs and skips in-flight items; phase 2 reads the durable actor state and resumes from the exact phase.
- **Concurrent items** — phase 1 is strictly sequential. Phase 2 keeps the lifecycle service as the dispatcher but offloads per-item execution to Dapr Workflow instances.
- **Retry policy on failure** — phase 1 hands the item back to `Ready` with a comment and stops. Phase 2 has actor-tracked retry counts and a give-up rule.
- **`Done` gate including branch-protection check** — phase 1 uses `Merged && ChecksGreen && Review==Approved`. Phase 2 also confirms branch-protection requirements are satisfied.
- **Compaction step** — phase 2 writes compacted memory to Dapr state after `Done`.
- **Dashboard-driven control (pause/resume/cancel)** — operator can stop the process; finer control is phase 2.

## Verification

- Unit tests on `TaskExecutor` with fakes for `IGitHubProjectService`, `IWorkspaceManager`, `IGitClient`, `IAgentRunner`:
  - happy path: Ready → InProgress → InReview → Done with one round.
  - ChangesRequested round: two `agentRunner.RunAsync` calls, the second with `PriorReviewFeedback` non-null, second result returns the same PR number.
  - Agent returns `Completed` but `PullRequest is null` → item released to Ready, comment posted, returns `Failed`.
  - Agent returns `SandboxViolation` → released to Ready, comment posted, returns `Failed`.
  - Cancellation mid-review-wait → `OperationCanceledException` propagates, no state changes happen after cancellation.
- An end-to-end test (manual, drives the running app) walks the phase-1 acceptance scenario from `00-roadmap.md`.
