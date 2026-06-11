# DeveloperAgent self-merges approved PRs — design

**Date:** 2026-06-11
**Status:** Approved (ready for implementation planning)

## Problem

Today the `DeveloperAgent` workflow opens a PR, moves the project item to **In-review**, then
**polls and waits for the PR to be merged by someone else** (a human, or GitHub auto-merge). The
completion trigger is `Merged && Approved`:

- `DeveloperTaskWorkflow.cs:191` — the direct-poll decision.
- `DeveloperTaskWorkflow.cs:234` — the event-race winner (`winner == mergedEventTask`).
- `WaitForReviewActivity.cs:75` — the event-raise condition (raises `Merged` when `Merged && Approved`).

The agent itself never merges. The separate `ReviewerAgent` process posts Approve / RequestChanges
reviews but also never merges. The result: after a PR is approved, work stalls until a human merges.

We want the `DeveloperAgent` to **squash-merge the approved PR itself, delete the branch, and move
on to the next item** — and to behave sensibly when the merge fails.

## Goal

When the workflow observes that a PR is **Approved** (and is actually mergeable), it should:

1. **Squash-merge** the PR.
2. **Delete** the remote head branch.
3. Move the item to **Done** and let the lifecycle loop pick up the next `Ready` item.

When the merge genuinely cannot proceed, it should fail gracefully (comment + leave for a human)
rather than blocking or looping forever.

## Decisions (confirmed with the user)

1. **Mechanism:** self-merge inside the workflow (a new activity), *not* GitHub-native auto-merge.
   Rationale: the user explicitly wants to "consider what to do if merge fails," and GitHub
   auto-merge's failure mode is silent (it just never fires, with no hook to react). Self-merge
   gives an explicit failure branch.
2. **Merge gate:** only merge when **Approved AND checks green AND GitHub reports the PR mergeable**.
   While checks are pending or mergeability is still being computed, **keep polling** — do not treat
   that as a failure.
3. **Hard-failure policy (conflict / branch protection blocks the merge):** post a comment on the
   PR explaining the failure, **leave the item in In-review** for a human (no board transition),
   release the workspace, and let the lifecycle loop move on to the next item.

## Architecture

The change respects the repo's generic-mechanics-vs-policy split (see `CLAUDE.md`):

### `Agent.GitHub` (agent-neutral mechanics)

- **`PullRequestStatus`** gains a `bool? Mergeable` field, sourced from Octokit's
  `PullRequest.Mergeable`. It is `null` while GitHub is still computing mergeability — the workflow
  treats `null` as "not yet known, keep polling." `WaitForReviewResult` (the activity's return type
  consumed by the workflow's *direct-poll* decision) carries the same `bool? Mergeable`, so the
  workflow can branch on mergeability without an extra round-trip — it already carries `ChecksGreen`.
- **`IGitHubProjectsClient.MergePullRequestAsync(int pullRequestNumber, PullRequestMergeMethod method,
  CancellationToken ct)`** returns a result that distinguishes the outcomes the policy layer must
  branch on:
  - `Merged` — the merge succeeded this call.
  - `AlreadyMerged` — the PR was already merged (idempotent success).
  - `NotMergeable` — GitHub refused because the PR is not in a mergeable state (conflict / blocked /
    checks). A hard failure for the policy layer.
  The merge **method** is a parameter (squash is policy, supplied by the host) so the library carries
  no opinion. A small `PullRequestMergeMethod` enum (e.g. `Merge`, `Squash`, `Rebase`) lives in
  `Agent.GitHub` so Octokit's enum does not leak.
- **`IGitHubProjectsClient.DeleteBranchAsync(string branchName, CancellationToken ct)`** deletes the
  remote head branch and **tolerates "already deleted" (404) as success**.
- **`IRestTransport`** gains the two corresponding methods wrapping Octokit `PullRequest.Merge`
  (with `MergePullRequest { MergeMethod = ... }`) and `Git.Reference.Delete(owner, repo,
  "heads/{branch}")`. Octokit types stay inside the transport (the existing boundary rule).

### `DeveloperAgent` (host policy)

- **`IGitHubProjectService`** facade gains:
  - `SquashMergePullRequestAsync(int pullRequestNumber, CancellationToken ct)` — calls the client
    with the squash method (the host owns the "squash" choice).
  - `DeleteBranchAsync(string branchName, CancellationToken ct)`.
  These pass through `GitHubProjectService` to the library client.
- **`MergePullRequestActivity`** (new Dapr activity) — squash-merges, then deletes the head branch.
  Returns a result the workflow branches on (merged-ok vs. not-mergeable). See "Idempotency" below.
- **`DeveloperTaskWorkflow`** — the trigger flip and the new gate/failure policy.

## Workflow changes

### Trigger flip + event rename

The external event `Merged` is renamed to **`ReadyToMerge`** because its meaning changes (it now
fires on "approved & mergeable," not "already merged"). `WaitForReviewActivity` raises
`ReadyToMerge` when the poll observes **Approved AND checks green AND mergeable == true**.

### Review-loop gate (per poll, when review state is `Approved`)

- **green + mergeable == true** → call `MergePullRequestActivity`:
  - success / already-merged → `CompleteWithSuccessAsync` (→ Done, compact memory, delete session).
  - not-mergeable → `CompleteWithMergeFailureAsync` (see below).
- **checks pending, or mergeable == null** → fall through to the cadence timer and re-poll. Not a
  failure.
- **mergeable == false** → `CompleteWithMergeFailureAsync` (definite conflict; no point looping).

`ChangesRequested` handling is unchanged (still loops into `ModifyCodeActivity`).

### New `CompleteWithMergeFailureAsync`

1. Post a comment on the PR/issue node explaining the failure (conflict vs. blocked).
2. Leave the item in **In-review** — no board transition (reuses the `DoneActivity` "failure with PR
   open" precedent at `DoneActivity.cs:92-99`; the merge-failure path will route through `DoneActivity`
   with `Success: false` and a non-null `PullRequestNumber`, which already means "leave In-review").
3. Release the workspace.
4. Return a `TaskResult("MergeFailed")`.

The lifecycle loop then picks up the next `Ready` item — no extra code needed for "continue with
next item."

## Idempotency (the key correctness risk)

This is a Dapr workflow: activities run under `retryOptions`, the body replays on recovery, and a
`RecoveryAlreadyMerged` fast-path already exists (`DeveloperTaskWorkflow.cs:73`). The merge activity
**will** sometimes run against an already-merged PR (its own retry after a lost response, or replay).
Octokit's `PullRequest.Merge` throws when the PR is already merged. Therefore:

- `MergePullRequestActivity` / the client merge method **treat "already merged" as success**
  (`AlreadyMerged`), not an error.
- `DeleteBranchAsync` **treats a missing branch (404) as success**.

Both are explicit test cases.

## Testing (TDD, red-green-refactor per `CLAUDE.md`)

**`Agent.GitHub.Tests`**
- Merge maps to the squash method on the transport.
- Merge against an already-merged PR → `AlreadyMerged` result (no throw).
- Merge refused by GitHub (not mergeable) → `NotMergeable` result.
- `DeleteBranchAsync` tolerates a 404 (already deleted).
- `PullRequestStatus.Mergeable` is surfaced from the PR fetch.

**`DeveloperAgent.Tests` (workflow + activity + facade)**
- Approved + green + mergeable → `MergePullRequestActivity` invoked → Done.
- Approved + checks pending (or `mergeable == null`) → loop continues, no merge attempted.
- Approved + `mergeable == false` → merge-failure path: comment posted, item left In-review, next
  item picked up.
- `MergePullRequestActivity` idempotent: already-merged PR → success; branch already deleted → success.
- `GitHubProjectService.SquashMergePullRequestAsync` calls the client with the squash method.
- Update `WaitForReviewActivityTests` and `DeveloperTaskWorkflowReviewLoopTests` for the
  `ReadyToMerge` event name and the new trigger condition.

## Out of scope

- Conflict auto-resolution (looping a merge conflict back into `ModifyCodeActivity` to rebase/resolve).
  Considered and explicitly deferred — materially more complex.
- Changing the `ReviewerAgent`. It keeps posting Approve / RequestChanges; the DeveloperAgent
  workflow is the only thing that merges.

## Implementation notes

- Verify the Octokit `PullRequest.Merge` (squash) and `Git.Reference.Delete` signatures via the
  Context7 MCP server before coding — those APIs drift (per the repo's documentation-lookup rule).
- Keep `Agent.GitHub` policy-free: the merge **method** and the "leave In-review on failure" decision
  are host concerns; the library only exposes the capability and the outcome.
