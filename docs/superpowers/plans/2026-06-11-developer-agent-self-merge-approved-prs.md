# DeveloperAgent self-merges approved PRs — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When the DeveloperAgent workflow observes that a PR is approved, checks are green, and the PR is mergeable, it squash-merges the PR itself, deletes the head branch, and moves the item to Done — and on a hard merge failure it comments and leaves the item In-review for a human.

**Architecture:** Generic GitHub merge/delete mechanics go in the agent-neutral `Agent.GitHub` library (a `MergeMethod` parameter + a `MergeOutcome` result keep it policy-free; idempotency orchestration lives in the testable `GitHubProjectsClient`, with Octokit confined to the transport). The developer-agent policy (squash choice, the gate, the failure handling) lives in `DeveloperAgent`: a facade method, a new `MergePullRequestActivity`, and the review-loop wiring in `DeveloperTaskWorkflow`.

**Tech Stack:** .NET 10, Dapr Workflow, Octokit / Octokit.GraphQL, xUnit + NSubstitute + FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-06-11-developer-agent-self-merge-approved-prs-design.md`

---

## Conventions for every task

- Solution file: `src/ClaudeAgentsSolo.slnx`.
- Build a single test project fast, e.g.:
  `dotnet test src/Tests/Agent.GitHub.Tests/Agent.GitHub.Tests.csproj`
- Full fast loop (excludes integration): `dotnet test src/ClaudeAgentsSolo.slnx --filter "Category!=Integration"`
- TDD: write the failing test first, run it red, implement minimally, run it green, commit.
- Commit messages use the existing style; end the body with the `Co-Authored-By` trailer used in this repo.

---

## File Structure

**`src/AgenticTools/Agent.GitHub/`** (agent-neutral mechanics)
- `GitHubModels.cs` — add `bool? Mergeable` to `PullRequestStatus`; add `enum MergeMethod`; add `enum MergeOutcome`.
- `IGitHubProjectsClient.cs` — add `MergePullRequestAsync` + `DeleteBranchAsync`.
- `Transports.cs` — add `Mergeable` to `RestPullRequest`; add `MergePullRequestAsync` + `DeleteBranchAsync` to `IRestTransport` and `OctokitRestTransport` (Octokit stays here).
- `GitHubProjectsClient.cs` — surface `Mergeable`; implement merge orchestration (get-PR-first → `AlreadyMerged`) + delete pass-through.

**`src/DeveloperAgent/`** (host policy)
- `GitHub/IGitHubProjectService.cs` + `GitHub/GitHubProjectService.cs` — add `SquashMergePullRequestAsync` + `DeleteBranchAsync`.
- `Workflow/WorkflowModels.cs` — add `Mergeable` to `WaitForReviewResult`; add `MergePullRequestActivityInput` + `MergePullRequestResult`; extend the `TaskResult` outcome doc with `"MergeFailed"`.
- `Workflow/Activities/MergePullRequestActivity.cs` — **new**: squash-merge, delete branch, comment on hard failure.
- `Workflow/Activities/WaitForReviewActivity.cs` — set `Mergeable`; rename the `Merged` event to `ReadyToMerge`; raise it on approved+green+mergeable.
- `Workflow/DeveloperTaskWorkflow.cs` — gate + merge wiring + `CompleteWithMergeAsync` / `CompleteWithMergeFailureAsync`.
- `Workflow/WorkflowServiceCollectionExtensions.cs` — register `MergePullRequestActivity`.

**Tests** — `Agent.GitHub.Tests`, `DeveloperAgent.Tests` (new `MergePullRequestActivityTests`; updates to `WaitForReviewActivityTests`, `DeveloperTaskWorkflowReviewLoopTests`, `DeveloperTaskWorkflowSavesAgentSessionTests`, `DeveloperTaskWorkflowTriageTests`, `RetryPolicyTests`, `WorkflowModelsTests`, `GitHubProjectServiceTests`, `DeveloperTaskWorkflowRegistrationTests`, `ActivityDependencyInjectionTests`).

---

## Task 1: Surface PR mergeability in `Agent.GitHub`

**Files:**
- Modify: `src/AgenticTools/Agent.GitHub/GitHubModels.cs`
- Modify: `src/AgenticTools/Agent.GitHub/Transports.cs`
- Modify: `src/AgenticTools/Agent.GitHub/GitHubProjectsClient.cs`
- Test: `src/Tests/Agent.GitHub.Tests/GitHubProjectsClientTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `GitHubProjectsClientTests.cs`. It fakes `IRestTransport` so the PR's mergeability flows through `GetPullRequestStatusAsync`. Note `RestPullRequest`/`IRestTransport` are `internal`; the test project already has `InternalsVisibleTo` (existing tests construct transport DTOs). Use the existing review/check-run fakes returning empty lists.

```csharp
[Fact]
public async Task GetPullRequestStatusAsync_surfaces_Mergeable_from_the_pull_request()
{
    var graphQL = Substitute.For<IGraphQLTransport>();
    var rest = Substitute.For<IRestTransport>();
    rest.GetPullRequestAsync("test-org", "test-repo", 7, Arg.Any<CancellationToken>())
        .Returns(new RestPullRequest(7, "sha7", "https://gh/pr/7", Merged: false, Mergeable: true));
    rest.GetPullRequestReviewsAsync("test-org", "test-repo", 7, Arg.Any<CancellationToken>())
        .Returns(Array.Empty<RestPullRequestReview>());
    rest.GetCheckRunsAsync("test-org", "test-repo", "sha7", Arg.Any<CancellationToken>())
        .Returns(Array.Empty<RestCheckRun>());

    var client = CreateClient(graphQL, rest);

    var status = await client.GetPullRequestStatusAsync(7, CancellationToken.None);

    status.Mergeable.Should().BeTrue();
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/Tests/Agent.GitHub.Tests/Agent.GitHub.Tests.csproj --filter "FullyQualifiedName~GetPullRequestStatusAsync_surfaces_Mergeable"`
Expected: FAIL — compile error (`RestPullRequest` has no `Mergeable`; `PullRequestStatus` has no `Mergeable`).

- [ ] **Step 3: Add `Mergeable` to `RestPullRequest`**

In `Transports.cs`, change the record (append an optional trailing field so the other constructors — create/find — keep compiling):

```csharp
internal sealed record RestPullRequest(
    int Number,
    string HeadSha,
    string HtmlUrl,
    bool Merged,
    bool? Mergeable = null);
```

In `OctokitRestTransport.GetPullRequestAsync`, populate it from Octokit:

```csharp
public async Task<RestPullRequest> GetPullRequestAsync(string owner, string repo, int number, CancellationToken ct)
{
    var pr = await GetClient().PullRequest.Get(owner, repo, number).ConfigureAwait(false);
    return new RestPullRequest(pr.Number, pr.Head.Sha, pr.HtmlUrl, pr.Merged, pr.Mergeable);
}
```

- [ ] **Step 4: Add `Mergeable` to `PullRequestStatus` (required, appended)**

In `GitHubModels.cs`, append the field and document it:

```csharp
/// <summary>Combined status snapshot for a pull request.</summary>
/// <param name="Number">PR number.</param>
/// <param name="Review">Aggregated review verdict (most recent non-dismissed review per reviewer).</param>
/// <param name="ChecksGreen">True when every check conclusion is in {success, neutral, skipped}.</param>
/// <param name="Merged">True when the PR has been merged.</param>
/// <param name="HeadSha">HEAD commit SHA of the head branch at the time this was fetched.</param>
/// <param name="Mergeable">GitHub's mergeability flag: <c>true</c> mergeable, <c>false</c> conflicting,
/// <c>null</c> while GitHub is still computing it (treat null as "not yet known").</param>
public sealed record PullRequestStatus(
    int Number,
    PullRequestReviewState Review,
    bool ChecksGreen,
    bool Merged,
    string HeadSha,
    bool? Mergeable);
```

In `GitHubProjectsClient.GetPullRequestStatusAsync`, pass it through:

```csharp
return new PullRequestStatus(
    Number: pullRequestNumber,
    Review: reviewState,
    ChecksGreen: checksGreen,
    Merged: pr.Merged,
    HeadSha: pr.HeadSha,
    Mergeable: pr.Mergeable);
```

`Mergeable` is **required** on `PullRequestStatus`. This is deliberate: it forces every construction site to be updated (a compile error) rather than silently defaulting — see Task 5 where a missing value would otherwise hang the workflow. Fix any other `new PullRequestStatus(...)` in `Agent.GitHub` if the compiler flags them (there should be none beyond the one above).

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test src/Tests/Agent.GitHub.Tests/Agent.GitHub.Tests.csproj --filter "FullyQualifiedName~GetPullRequestStatusAsync_surfaces_Mergeable"`
Expected: PASS. If other tests in this project fail to compile because they construct `PullRequestStatus` positionally, append the new mergeability argument to those calls (search the test project for `new PullRequestStatus(`).

- [ ] **Step 6: Commit**

```bash
git add src/AgenticTools/Agent.GitHub src/Tests/Agent.GitHub.Tests
git commit -m "Surface PR mergeability in Agent.GitHub PullRequestStatus"
```

---

## Task 2: Merge + delete-branch capability in `Agent.GitHub`

**Files:**
- Modify: `src/AgenticTools/Agent.GitHub/GitHubModels.cs`
- Modify: `src/AgenticTools/Agent.GitHub/IGitHubProjectsClient.cs`
- Modify: `src/AgenticTools/Agent.GitHub/Transports.cs`
- Modify: `src/AgenticTools/Agent.GitHub/GitHubProjectsClient.cs`
- Test: `src/Tests/Agent.GitHub.Tests/GitHubProjectsClientMergeTests.cs` (new)

- [ ] **Step 1: Add the `MergeMethod` and `MergeOutcome` enums**

In `GitHubModels.cs` (named `MergeMethod`, not `PullRequestMergeMethod`, to avoid clashing with Octokit's enum inside the transport):

```csharp
/// <summary>How a pull request is merged. Maps to GitHub's three merge methods.</summary>
public enum MergeMethod { Merge, Squash, Rebase }

/// <summary>Outcome of an attempt to merge a pull request.</summary>
/// <remarks>
/// <see cref="Merged"/> and <see cref="AlreadyMerged"/> are both successes — the latter makes the
/// operation idempotent under workflow retry/replay. <see cref="NotMergeable"/> is a hard failure
/// (conflict, failing required checks, or branch protection refusing the merge).
/// </remarks>
public enum MergeOutcome { Merged, AlreadyMerged, NotMergeable }
```

- [ ] **Step 2: Write the failing client tests**

Create `src/Tests/Agent.GitHub.Tests/GitHubProjectsClientMergeTests.cs`. These assert the **idempotency orchestration** that lives in the client: it fetches the PR first and treats an already-merged PR as success without calling merge.

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Agent.GitHub.Tests;

public sealed class GitHubProjectsClientMergeTests
{
    private static GitHubProjectsClient CreateClient(IRestTransport rest) =>
        new(Substitute.For<IGraphQLTransport>(), rest,
            Options.Create(new GitHubOptions
            {
                Owner = "test-org",
                Repository = new RepositoryOptions { Name = "test-repo", DefaultBranch = "main" },
                Project = new ProjectOptions { Number = 1, OwnerType = "Organization" }
            }),
            NullLogger<GitHubProjectsClient>.Instance);

    [Fact]
    public async Task MergePullRequestAsync_returns_AlreadyMerged_without_calling_merge_when_PR_is_merged()
    {
        var rest = Substitute.For<IRestTransport>();
        rest.GetPullRequestAsync("test-org", "test-repo", 7, Arg.Any<CancellationToken>())
            .Returns(new RestPullRequest(7, "sha7", "url", Merged: true, Mergeable: null));

        var client = CreateClient(rest);

        var outcome = await client.MergePullRequestAsync(7, MergeMethod.Squash, CancellationToken.None);

        outcome.Should().Be(MergeOutcome.AlreadyMerged);
        await rest.DidNotReceiveWithAnyArgs()
            .MergePullRequestAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task MergePullRequestAsync_calls_transport_with_squash_and_returns_Merged()
    {
        var rest = Substitute.For<IRestTransport>();
        rest.GetPullRequestAsync("test-org", "test-repo", 7, Arg.Any<CancellationToken>())
            .Returns(new RestPullRequest(7, "sha7", "url", Merged: false, Mergeable: true));
        rest.MergePullRequestAsync("test-org", "test-repo", 7, MergeMethod.Squash, Arg.Any<CancellationToken>())
            .Returns(MergeOutcome.Merged);

        var client = CreateClient(rest);

        var outcome = await client.MergePullRequestAsync(7, MergeMethod.Squash, CancellationToken.None);

        outcome.Should().Be(MergeOutcome.Merged);
        await rest.Received(1).MergePullRequestAsync(
            "test-org", "test-repo", 7, MergeMethod.Squash, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MergePullRequestAsync_returns_NotMergeable_when_transport_reports_it()
    {
        var rest = Substitute.For<IRestTransport>();
        rest.GetPullRequestAsync("test-org", "test-repo", 7, Arg.Any<CancellationToken>())
            .Returns(new RestPullRequest(7, "sha7", "url", Merged: false, Mergeable: false));
        rest.MergePullRequestAsync("test-org", "test-repo", 7, MergeMethod.Squash, Arg.Any<CancellationToken>())
            .Returns(MergeOutcome.NotMergeable);

        var client = CreateClient(rest);

        var outcome = await client.MergePullRequestAsync(7, MergeMethod.Squash, CancellationToken.None);

        outcome.Should().Be(MergeOutcome.NotMergeable);
    }

    [Fact]
    public async Task DeleteBranchAsync_delegates_to_transport_with_configured_repo()
    {
        var rest = Substitute.For<IRestTransport>();
        var client = CreateClient(rest);

        await client.DeleteBranchAsync("agent/feature-x", CancellationToken.None);

        await rest.Received(1).DeleteBranchAsync(
            "test-org", "test-repo", "agent/feature-x", Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test src/Tests/Agent.GitHub.Tests/Agent.GitHub.Tests.csproj --filter "FullyQualifiedName~GitHubProjectsClientMergeTests"`
Expected: FAIL — compile errors (`MergePullRequestAsync` / `DeleteBranchAsync` not defined on the client or transport).

- [ ] **Step 4: Extend the interfaces**

In `IGitHubProjectsClient.cs`, add:

```csharp
/// <summary>
/// Merges the pull request using <paramref name="method"/>. Idempotent: an already-merged PR
/// returns <see cref="MergeOutcome.AlreadyMerged"/> instead of throwing, so a workflow retry or
/// replay is safe. A PR GitHub refuses to merge returns <see cref="MergeOutcome.NotMergeable"/>.
/// </summary>
Task<MergeOutcome> MergePullRequestAsync(int pullRequestNumber, MergeMethod method, CancellationToken ct);

/// <summary>Deletes the head branch <paramref name="branchName"/>. A missing branch is a no-op (idempotent).</summary>
Task DeleteBranchAsync(string branchName, CancellationToken ct);
```

In `Transports.cs`, add to `IRestTransport`:

```csharp
/// <summary>
/// Attempts the merge. Returns <see cref="MergeOutcome.Merged"/> on success or
/// <see cref="MergeOutcome.NotMergeable"/> when GitHub refuses (405). Never returns AlreadyMerged —
/// that decision is the client's (it checks the PR first).
/// </summary>
Task<MergeOutcome> MergePullRequestAsync(string owner, string repo, int number, MergeMethod method, CancellationToken ct);

/// <summary>Deletes the branch ref. Treats a missing ref (404) as success.</summary>
Task DeleteBranchAsync(string owner, string repo, string branchName, CancellationToken ct);
```

- [ ] **Step 5: Implement the transport methods (Octokit confined here)**

In `OctokitRestTransport`, add:

```csharp
public async Task<MergeOutcome> MergePullRequestAsync(
    string owner, string repo, int number, MergeMethod method, CancellationToken ct)
{
    var request = new MergePullRequest { MergeMethod = ToOctokitMergeMethod(method) };
    try
    {
        await GetClient().PullRequest.Merge(owner, repo, number, request).ConfigureAwait(false);
        return MergeOutcome.Merged;
    }
    catch (PullRequestNotMergeableException)
    {
        // GitHub returns 405 when the PR cannot be merged: conflict with base, failing required
        // checks, or branch protection. The client turns this into the workflow's failure path.
        return MergeOutcome.NotMergeable;
    }
}

public async Task DeleteBranchAsync(string owner, string repo, string branchName, CancellationToken ct)
{
    try
    {
        await GetClient().Git.Reference.Delete(owner, repo, $"heads/{branchName}").ConfigureAwait(false);
    }
    catch (NotFoundException)
    {
        // Branch already gone (e.g. a retried delete, or GitHub auto-deleted it). Idempotent success.
        // Mirrors the NotFoundException-swallow in RepositoryExistsAsync.
    }
}

private static Octokit.PullRequestMergeMethod ToOctokitMergeMethod(MergeMethod method) => method switch
{
    MergeMethod.Merge => Octokit.PullRequestMergeMethod.Merge,
    MergeMethod.Squash => Octokit.PullRequestMergeMethod.Squash,
    MergeMethod.Rebase => Octokit.PullRequestMergeMethod.Rebase,
    _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
};
```

- [ ] **Step 6: Implement the client methods (testable idempotency orchestration)**

In `GitHubProjectsClient.cs`, add (place near `GetPullRequestStatusAsync`):

```csharp
public async Task<MergeOutcome> MergePullRequestAsync(int pullRequestNumber, MergeMethod method, CancellationToken ct)
{
    // Idempotency: a retried or replayed activity can run against a PR that is already merged.
    // Octokit throws if you merge an already-merged PR, so check first and short-circuit.
    var pr = await _rest.GetPullRequestAsync(_options.Owner, _options.Repository.Name, pullRequestNumber, ct)
        .ConfigureAwait(false);
    if (pr.Merged)
    {
        _logger.LogInformation("PR #{Number} already merged; treating merge as a no-op.", pullRequestNumber);
        return MergeOutcome.AlreadyMerged;
    }

    _logger.LogInformation("Merging PR #{Number} via {Method}.", pullRequestNumber, method);
    return await _rest.MergePullRequestAsync(
        _options.Owner, _options.Repository.Name, pullRequestNumber, method, ct).ConfigureAwait(false);
}

public Task DeleteBranchAsync(string branchName, CancellationToken ct)
    => _rest.DeleteBranchAsync(_options.Owner, _options.Repository.Name, branchName, ct);
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test src/Tests/Agent.GitHub.Tests/Agent.GitHub.Tests.csproj`
Expected: PASS (whole project).

- [ ] **Step 8: Commit**

```bash
git add src/AgenticTools/Agent.GitHub src/Tests/Agent.GitHub.Tests
git commit -m "Add squash-merge + delete-branch to Agent.GitHub (idempotent)"
```

---

## Task 3: Facade methods in `DeveloperAgent`

**Files:**
- Modify: `src/DeveloperAgent/GitHub/IGitHubProjectService.cs`
- Modify: `src/DeveloperAgent/GitHub/GitHubProjectService.cs`
- Test: `src/Tests/DeveloperAgent.Tests/GitHub/GitHubProjectServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `GitHubProjectServiceTests.cs`. The facade wraps `IGitHubProjectsClient`; assert the squash facade passes `MergeMethod.Squash` and that delete is a pass-through. Match the existing construction pattern in that file (it already builds a `GitHubProjectService` with a substituted `IGitHubProjectsClient` and `Options.Create(new ProjectStateNames{...})` — reuse the existing helper if present).

```csharp
[Fact]
public async Task SquashMergePullRequestAsync_calls_client_with_Squash_method()
{
    var client = Substitute.For<IGitHubProjectsClient>();
    client.MergePullRequestAsync(7, MergeMethod.Squash, Arg.Any<CancellationToken>())
        .Returns(MergeOutcome.Merged);
    var service = CreateService(client); // existing helper; or: new GitHubProjectService(client, Options.Create(DefaultStateNames()))

    var outcome = await service.SquashMergePullRequestAsync(7, CancellationToken.None);

    outcome.Should().Be(MergeOutcome.Merged);
    await client.Received(1).MergePullRequestAsync(7, MergeMethod.Squash, Arg.Any<CancellationToken>());
}

[Fact]
public async Task DeleteBranchAsync_is_a_pass_through_to_the_client()
{
    var client = Substitute.For<IGitHubProjectsClient>();
    var service = CreateService(client);

    await service.DeleteBranchAsync("agent/feature-x", CancellationToken.None);

    await client.Received(1).DeleteBranchAsync("agent/feature-x", Arg.Any<CancellationToken>());
}
```

If `GitHubProjectServiceTests.cs` has no `CreateService` helper, construct directly:
`new GitHubProjectService(client, Options.Create(new ProjectStateNames()))` (defaults are fine — these tests don't touch state-name mapping).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Tests/DeveloperAgent.Tests/DeveloperAgent.Tests.csproj --filter "FullyQualifiedName~SquashMergePullRequestAsync_calls_client_with_Squash_method|FullyQualifiedName~DeleteBranchAsync_is_a_pass_through"`
Expected: FAIL — `SquashMergePullRequestAsync` / `DeleteBranchAsync` not defined on `IGitHubProjectService`.

- [ ] **Step 3: Extend the facade interface**

In `IGitHubProjectService.cs`, add:

```csharp
/// <summary>
/// Squash-merges the pull request (the developer-agent's chosen merge method). Idempotent:
/// an already-merged PR returns <see cref="MergeOutcome.AlreadyMerged"/>.
/// </summary>
Task<MergeOutcome> SquashMergePullRequestAsync(int pullRequestNumber, CancellationToken ct);

/// <summary>Deletes the head branch. A missing branch is a no-op.</summary>
Task DeleteBranchAsync(string branchName, CancellationToken ct);
```

- [ ] **Step 4: Implement on the facade**

In `GitHubProjectService.cs`, add to the pass-through section:

```csharp
public Task<MergeOutcome> SquashMergePullRequestAsync(int pullRequestNumber, CancellationToken ct)
    => _client.MergePullRequestAsync(pullRequestNumber, MergeMethod.Squash, ct);

public Task DeleteBranchAsync(string branchName, CancellationToken ct)
    => _client.DeleteBranchAsync(branchName, ct);
```

(`MergeMethod` / `MergeOutcome` resolve via the `global using Agent.GitHub` in `DeveloperAgent`.)

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/Tests/DeveloperAgent.Tests/DeveloperAgent.Tests.csproj --filter "FullyQualifiedName~SquashMergePullRequestAsync_calls_client_with_Squash_method|FullyQualifiedName~DeleteBranchAsync_is_a_pass_through"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/DeveloperAgent/GitHub src/Tests/DeveloperAgent.Tests/GitHub
git commit -m "Add SquashMerge + DeleteBranch to the GitHubProjectService facade"
```

---

## Task 4: `MergePullRequestActivity` (squash + delete + comment-on-failure)

**Files:**
- Modify: `src/DeveloperAgent/Workflow/WorkflowModels.cs`
- Create: `src/DeveloperAgent/Workflow/Activities/MergePullRequestActivity.cs`
- Modify: `src/DeveloperAgent/Workflow/WorkflowServiceCollectionExtensions.cs`
- Test: `src/Tests/DeveloperAgent.Tests/Workflow/MergePullRequestActivityTests.cs` (new)

- [ ] **Step 1: Add the activity input/result records**

In `WorkflowModels.cs`, add to the per-activity input/result sections:

```csharp
/// <summary>Input for <see cref="Activities.MergePullRequestActivity"/>.</summary>
public sealed record MergePullRequestActivityInput(
    string ProjectItemId,
    string ContentNodeId,
    int PullRequestNumber,
    string BranchName);

/// <summary>Result of <see cref="Activities.MergePullRequestActivity"/>.</summary>
/// <param name="Outcome">Merged/AlreadyMerged → success; NotMergeable → the workflow's failure path.</param>
public sealed record MergePullRequestResult(MergeOutcome Outcome)
{
    /// <summary>True when the PR ended up merged (this call or already).</summary>
    public bool Succeeded => Outcome is MergeOutcome.Merged or MergeOutcome.AlreadyMerged;
}
```

- [ ] **Step 2: Write the failing tests**

Create `src/Tests/DeveloperAgent.Tests/Workflow/MergePullRequestActivityTests.cs`:

```csharp
using DeveloperAgent.GitHub;
using DeveloperAgent.Workflow;
using DeveloperAgent.Workflow.Activities;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeveloperAgent.Tests.Workflow;

public sealed class MergePullRequestActivityTests
{
    private static MergePullRequestActivityInput Input(int pr = 7) =>
        new(ProjectItemId: "PVTI_abc", ContentNodeId: "PR_node", PullRequestNumber: pr, BranchName: "agent/feature-x");

    // The existing FakeWaitForReviewActivityContext is `file`-scoped in another file (not visible
    // here), so define a local one at the bottom of this file.
    private static WorkflowActivityContext Ctx() => new FakeMergeActivityContext();

    [Fact]
    public async Task Merges_then_deletes_branch_on_success()
    {
        var github = Substitute.For<IGitHubProjectService>();
        github.SquashMergePullRequestAsync(7, Arg.Any<CancellationToken>()).Returns(MergeOutcome.Merged);

        var activity = new MergePullRequestActivity(NullLogger<MergePullRequestActivity>.Instance, github);
        var result = await activity.RunAsync(Ctx(), Input());

        result.Succeeded.Should().BeTrue();
        await github.Received(1).SquashMergePullRequestAsync(7, Arg.Any<CancellationToken>());
        await github.Received(1).DeleteBranchAsync("agent/feature-x", Arg.Any<CancellationToken>());
        await github.DidNotReceiveWithAnyArgs().AddItemCommentAsync(default!, default!, default);
    }

    [Fact]
    public async Task AlreadyMerged_is_treated_as_success_and_still_deletes_branch()
    {
        var github = Substitute.For<IGitHubProjectService>();
        github.SquashMergePullRequestAsync(7, Arg.Any<CancellationToken>()).Returns(MergeOutcome.AlreadyMerged);

        var activity = new MergePullRequestActivity(NullLogger<MergePullRequestActivity>.Instance, github);
        var result = await activity.RunAsync(Ctx(), Input());

        result.Succeeded.Should().BeTrue();
        await github.Received(1).DeleteBranchAsync("agent/feature-x", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotMergeable_comments_and_does_not_delete_branch()
    {
        var github = Substitute.For<IGitHubProjectService>();
        github.SquashMergePullRequestAsync(7, Arg.Any<CancellationToken>()).Returns(MergeOutcome.NotMergeable);

        var activity = new MergePullRequestActivity(NullLogger<MergePullRequestActivity>.Instance, github);
        var result = await activity.RunAsync(Ctx(), Input());

        result.Succeeded.Should().BeFalse();
        result.Outcome.Should().Be(MergeOutcome.NotMergeable);
        await github.Received(1).AddItemCommentAsync("PR_node", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await github.DidNotReceiveWithAnyArgs().DeleteBranchAsync(default!, default);
    }
}
```

Add `using Dapr.Workflow;` at the top of the file, and at the bottom of the file add the local fake context (`file` types are per-file, so this does not clash with the same-named pattern elsewhere):

```csharp
file sealed class FakeMergeActivityContext : WorkflowActivityContext
{
    public override Dapr.Workflow.Abstractions.TaskIdentifier Identifier => "fake-task-name";
    public override string InstanceId => "fake-instance-id";
    public override string TaskExecutionKey => "fake-task-key";
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test src/Tests/DeveloperAgent.Tests/DeveloperAgent.Tests.csproj --filter "FullyQualifiedName~MergePullRequestActivityTests"`
Expected: FAIL — `MergePullRequestActivity` does not exist.

- [ ] **Step 4: Implement the activity**

Create `src/DeveloperAgent/Workflow/Activities/MergePullRequestActivity.cs`:

```csharp
using Dapr.Workflow;
using DeveloperAgent.GitHub;
using Microsoft.Extensions.Logging;

namespace DeveloperAgent.Workflow.Activities;

/// <summary>
/// Squash-merges an approved pull request and deletes its head branch. Called by the review loop
/// once <see cref="WaitForReviewActivity"/> has observed the PR is approved, green, and mergeable.
/// </summary>
/// <remarks>
/// Idempotent by construction: <see cref="IGitHubProjectService.SquashMergePullRequestAsync"/> maps
/// an already-merged PR to <see cref="MergeOutcome.AlreadyMerged"/> and
/// <see cref="IGitHubProjectService.DeleteBranchAsync"/> tolerates a missing branch — so a workflow
/// retry or replay re-runs this activity safely. On a hard failure (<see cref="MergeOutcome.NotMergeable"/>)
/// the activity comments on the PR and leaves the branch alone; the workflow then parks the item in
/// In-review for a human.
/// </remarks>
public sealed class MergePullRequestActivity : WorkflowActivity<MergePullRequestActivityInput, MergePullRequestResult>
{
    private readonly ILogger<MergePullRequestActivity> _logger;
    private readonly IGitHubProjectService _github;

    public MergePullRequestActivity(ILogger<MergePullRequestActivity> logger, IGitHubProjectService github)
    {
        _logger = logger;
        _github = github;
    }

    public override async Task<MergePullRequestResult> RunAsync(
        WorkflowActivityContext context, MergePullRequestActivityInput input)
    {
        var ct = CancellationToken.None;

        var outcome = await _github.SquashMergePullRequestAsync(input.PullRequestNumber, ct);

        if (outcome == MergeOutcome.NotMergeable)
        {
            _logger.LogWarning(
                "[{Activity}] PR #{PrNumber} is not mergeable; leaving it for a human. item={ItemId}",
                nameof(MergePullRequestActivity), input.PullRequestNumber, input.ProjectItemId);

            await _github.AddItemCommentAsync(
                input.ContentNodeId,
                $"⚠️ Automated squash-merge of this PR failed: GitHub reports it is not mergeable " +
                "(merge conflict with the base branch, a failing required check, or branch protection). " +
                "The item has been left in **In-review** for a human to resolve.",
                ct);

            return new MergePullRequestResult(outcome);
        }

        _logger.LogInformation(
            "[{Activity}] PR #{PrNumber} merged ({Outcome}); deleting branch {Branch}. item={ItemId}",
            nameof(MergePullRequestActivity), input.PullRequestNumber, outcome, input.BranchName, input.ProjectItemId);

        await _github.DeleteBranchAsync(input.BranchName, ct);

        return new MergePullRequestResult(outcome);
    }
}
```

- [ ] **Step 5: Register the activity**

In `WorkflowServiceCollectionExtensions.cs`, add next to the other `RegisterActivity` calls (e.g. after `WaitForReviewActivity`):

```csharp
opt.RegisterActivity<MergePullRequestActivity>();
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test src/Tests/DeveloperAgent.Tests/DeveloperAgent.Tests.csproj --filter "FullyQualifiedName~MergePullRequestActivityTests"`
Expected: PASS.

- [ ] **Step 7: Update the registration/DI convention tests**

`DeveloperTaskWorkflowRegistrationTests.cs` and `ActivityDependencyInjectionTests.cs` enumerate the activity types. Add `MergePullRequestActivity` to their `[InlineData(...)]` / expected-type lists (search for `WaitForReviewActivity` in those files and add a sibling entry). `DeveloperTaskWorkflowRegistrationTests.cs:153` also has a string list of activity names — add `"MergePullRequestActivity"`.

Run: `dotnet test src/Tests/DeveloperAgent.Tests/DeveloperAgent.Tests.csproj --filter "FullyQualifiedName~Registration|FullyQualifiedName~ActivityDependencyInjection"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/DeveloperAgent/Workflow src/Tests/DeveloperAgent.Tests/Workflow
git commit -m "Add MergePullRequestActivity: squash-merge, delete branch, comment on failure"
```

---

## Task 5: Trigger flip — act on Approved, merge in the review loop

This is the integration task. It (a) adds `Mergeable` to `WaitForReviewResult`, (b) changes `WaitForReviewActivity` to raise a `ReadyToMerge` event on approved+green+mergeable, and (c) wires `DeveloperTaskWorkflow` to merge via the new activity. Because the new record field is **required**, the compiler will flag every test that constructs `WaitForReviewResult` — that is intentional (it prevents the silent workflow hang described in the spec).

**Files:**
- Modify: `src/DeveloperAgent/Workflow/WorkflowModels.cs`
- Modify: `src/DeveloperAgent/Workflow/Activities/WaitForReviewActivity.cs`
- Modify: `src/DeveloperAgent/Workflow/DeveloperTaskWorkflow.cs`
- Test: `WaitForReviewActivityTests.cs`, `DeveloperTaskWorkflowReviewLoopTests.cs`, `DeveloperTaskWorkflowSavesAgentSessionTests.cs`, `DeveloperTaskWorkflowTriageTests.cs`, `RetryPolicyTests.cs`, `WorkflowModelsTests.cs`

### Part A — `WaitForReviewResult.Mergeable` (no behavior change yet)

- [ ] **Step 1: Append `Mergeable` to `WaitForReviewResult`**

In `WorkflowModels.cs`:

```csharp
/// <summary>Result of <see cref="Activities.WaitForReviewActivity"/>.</summary>
/// <param name="Mergeable">GitHub mergeability: true mergeable, false conflicting, null still computing.</param>
public sealed record WaitForReviewResult(
    PullRequestReviewState ReviewState,
    bool Merged,
    bool ChecksGreen,
    string? FeedbackMarkdown,
    DateTimeOffset PolledAtUtc,
    bool? Mergeable);
```

- [ ] **Step 2: Set it in `WaitForReviewActivity`**

In `WaitForReviewActivity.RunAsync`, change the return to include mergeability:

```csharp
return new WaitForReviewResult(
    ReviewState: status.Review,
    Merged: status.Merged,
    ChecksGreen: status.ChecksGreen,
    FeedbackMarkdown: string.IsNullOrEmpty(feedbackMarkdown) ? null : feedbackMarkdown,
    PolledAtUtc: polledAt,
    Mergeable: status.Mergeable);
```

- [ ] **Step 3: Fix all `WaitForReviewResult` construction sites so the project compiles**

The compiler will flag each. Append the new trailing argument:
- `DeveloperTaskWorkflowReviewLoopTests.cs` lines ~47, 81, 82, 104, 106, 127, 129 — for `Pending`/`ChangesRequested` results pass `Mergeable: null` (or `false`); for the `Approved` results that should drive a merge pass `Mergeable: true`.
- `DeveloperTaskWorkflowSavesAgentSessionTests.cs` line ~37 — pass `Mergeable: true`.
- `DeveloperTaskWorkflowTriageTests.cs` line ~37 — pass `Mergeable: true`.
- `RetryPolicyTests.cs` lines ~39, 76 — `Approved` results pass `Mergeable: true`; the `ChangesRequested` at line ~75 passes `Mergeable: null`.
- `WorkflowModelsTests.cs` lines ~131, 139 — pass `Mergeable: true` / `Mergeable: null` respectively, and add a field to the round-trip assertion if the test compares all fields.
- `WaitForReviewActivityTests.cs` — these construct `PullRequestStatus` (Task 1 already required `Mergeable` there); ensure those calls pass a mergeability value too (`true` for the approved-merge case, `null`/`false` otherwise).

**Also seed the merge activity result in every `SetupHappyPath` helper that drives the workflow through the review loop to completion** (harmless now — it is unused until Part C flips the trigger — but required to avoid a `NullReferenceException` once the direct-poll path calls the merge activity and reads `default(MergePullRequestResult)!`). Add this line to the helpers in `DeveloperTaskWorkflowReviewLoopTests.cs` (`SetupHappyPathUntilReviewLoop`), `DeveloperTaskWorkflowSavesAgentSessionTests.cs` (`SetupHappyPath`), `DeveloperTaskWorkflowTriageTests.cs` (its happy-path setup), and `RetryPolicyTests.cs` (its setup):

```csharp
ctx.SetActivityResult(nameof(MergePullRequestActivity),
    new MergePullRequestResult(MergeOutcome.Merged));
```

Run: `dotnet build src/Tests/DeveloperAgent.Tests/DeveloperAgent.Tests.csproj`
Expected: BUILD SUCCEEDS (behavior unchanged so far — the workflow still completes via the old `Merged && Approved` path because that code hasn't changed yet). If any workflow test now hangs, you missed setting `Mergeable: true` on a success-path result OR the old path still keys on `Merged` (it does until Part C) — at this point tests should still be green.

- [ ] **Step 4: Run the affected tests to confirm still green**

Run: `dotnet test src/Tests/DeveloperAgent.Tests/DeveloperAgent.Tests.csproj --filter "FullyQualifiedName~Workflow"`
Expected: PASS (no behavior change yet).

- [ ] **Step 5: Commit**

```bash
git add src/DeveloperAgent/Workflow src/Tests/DeveloperAgent.Tests/Workflow
git commit -m "Carry PR mergeability through WaitForReviewResult"
```

### Part B — `WaitForReviewActivity` raises `ReadyToMerge` on approved+green+mergeable

- [ ] **Step 6: Update the activity's event tests**

In `WaitForReviewActivityTests.cs`:
- Rename `RunAsync_raises_Merged_event_when_PR_is_merged_and_approved` to `RunAsync_raises_ReadyToMerge_event_when_approved_green_and_mergeable`. Set the status to **not merged yet** but approved/green/mergeable, and assert the `"ReadyToMerge"` event is raised:

```csharp
[Fact]
public async Task RunAsync_raises_ReadyToMerge_event_when_approved_green_and_mergeable()
{
    var github = Substitute.For<IGitHubProjectService>();
    var workflowClient = Substitute.For<IDaprWorkflowClient>();
    github.GetPullRequestStatusAsync(7, Arg.Any<CancellationToken>())
        .Returns(new PullRequestStatus(7, PullRequestReviewState.Approved, ChecksGreen: true, Merged: false, "sha7", Mergeable: true));
    github.GetReviewFeedbackSinceAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
        .Returns(string.Empty);

    var activity = new WaitForReviewActivity(NullLogger<WaitForReviewActivity>.Instance, github, workflowClient);
    await activity.RunAsync(MakeContext(), MakeInput(7));

    await workflowClient.Received(1).RaiseEventAsync(
        FakeInstanceId, "ReadyToMerge", Arg.Any<object>(), Arg.Any<CancellationToken>());
}

[Fact]
public async Task RunAsync_does_not_raise_ReadyToMerge_when_checks_not_green()
{
    var github = Substitute.For<IGitHubProjectService>();
    var workflowClient = Substitute.For<IDaprWorkflowClient>();
    github.GetPullRequestStatusAsync(8, Arg.Any<CancellationToken>())
        .Returns(new PullRequestStatus(8, PullRequestReviewState.Approved, ChecksGreen: false, Merged: false, "sha8", Mergeable: true));
    github.GetReviewFeedbackSinceAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
        .Returns(string.Empty);

    var activity = new WaitForReviewActivity(NullLogger<WaitForReviewActivity>.Instance, github, workflowClient);
    await activity.RunAsync(MakeContext(), MakeInput(8));

    await workflowClient.DidNotReceiveWithAnyArgs().RaiseEventAsync(default!, default!, default!, default);
}

[Fact]
public async Task RunAsync_does_not_raise_ReadyToMerge_when_mergeability_unknown()
{
    var github = Substitute.For<IGitHubProjectService>();
    var workflowClient = Substitute.For<IDaprWorkflowClient>();
    github.GetPullRequestStatusAsync(9, Arg.Any<CancellationToken>())
        .Returns(new PullRequestStatus(9, PullRequestReviewState.Approved, ChecksGreen: true, Merged: false, "sha9", Mergeable: null));
    github.GetReviewFeedbackSinceAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
        .Returns(string.Empty);

    var activity = new WaitForReviewActivity(NullLogger<WaitForReviewActivity>.Instance, github, workflowClient);
    await activity.RunAsync(MakeContext(), MakeInput(9));

    await workflowClient.DidNotReceiveWithAnyArgs().RaiseEventAsync(default!, default!, default!, default);
}
```

- [ ] **Step 7: Run them red**

Run: `dotnet test src/Tests/DeveloperAgent.Tests/DeveloperAgent.Tests.csproj --filter "FullyQualifiedName~WaitForReviewActivityTests"`
Expected: FAIL — the activity still raises `"Merged"` and keys on `status.Merged`.

- [ ] **Step 8: Change the activity**

In `WaitForReviewActivity.cs`:
- Rename the constant and its value:

```csharp
/// <summary>Event raised when the PR is approved, green, and mergeable — i.e. ready to be merged.</summary>
public const string ReadyToMergeEventName = "ReadyToMerge";
```

(remove the old `MergedEventName` constant.)

- Change the raise condition (no longer requires `status.Merged`):

```csharp
var payload = new ReviewEventPayload(input.PullRequestNumber);
if (status.Review == PullRequestReviewState.Approved && status.ChecksGreen && status.Mergeable == true)
{
    await _workflowClient.RaiseEventAsync(
        instanceId: context.InstanceId,
        eventName: ReadyToMergeEventName,
        eventPayload: payload,
        cancellation: ct);
}
else if (status.Review == PullRequestReviewState.ChangesRequested)
{
    await _workflowClient.RaiseEventAsync(
        instanceId: context.InstanceId,
        eventName: ChangesRequestedEventName,
        eventPayload: payload,
        cancellation: ct);
}
```

Also update the XML `<remarks>` bullet that documents the `Merged` event to describe `ReadyToMerge`.

This will not compile yet: `DeveloperTaskWorkflow.cs:228` references `WaitForReviewActivity.MergedEventName`. That is fixed in Part C. To keep this step self-contained, also do Part C Step 11 (the workflow rename) before building. (Parts B and C share the rename; treat Steps 8–13 as one red→green block and commit once at the end of Part C.)

### Part C — `DeveloperTaskWorkflow` merges on the trigger

- [ ] **Step 9: Write the workflow tests**

In `DeveloperTaskWorkflowReviewLoopTests.cs` (the canned `MergePullRequestActivity` success result was already added to `SetupHappyPathUntilReviewLoop` in Part A Step 3), update the two existing event tests to the new event name + merge expectation, and add the new gate tests:

```csharp
[Fact]
public async Task Workflow_merges_then_completes_Done_when_ReadyToMerge_event_arrives()
{
    var ctx = new FakeWorkflowContext();
    SetupHappyPathUntilReviewLoop(ctx);
    ctx.SetReviewPollResults(
        new WaitForReviewResult(PullRequestReviewState.Pending, false, false, null, DateTimeOffset.UtcNow, Mergeable: null));
    ctx.CompleteExternalEvent("ReadyToMerge", new ReviewEventPayload(7));

    var workflow = new DeveloperTaskWorkflow();
    var result = await workflow.RunAsync(ctx, Input());

    ctx.ActivityCalls.Should().Contain(c => c.Name == nameof(MergePullRequestActivity));
    result.Outcome.Should().Be("Done");
    var doneCall = ctx.ActivityCalls.Last(c => c.Name == nameof(DoneActivity));
    ((DoneActivityInput)doneCall.Input!).Success.Should().BeTrue();
}

[Fact]
public async Task Workflow_merges_on_direct_poll_when_approved_green_and_mergeable()
{
    var ctx = new FakeWorkflowContext();
    SetupHappyPathUntilReviewLoop(ctx);
    ctx.SetReviewPollResults(
        new WaitForReviewResult(PullRequestReviewState.Approved, false, true, null, DateTimeOffset.UtcNow, Mergeable: true));

    var workflow = new DeveloperTaskWorkflow();
    var result = await workflow.RunAsync(ctx, Input());

    ctx.ActivityCalls.Should().Contain(c => c.Name == nameof(MergePullRequestActivity));
    var mergeCall = ctx.ActivityCalls.First(c => c.Name == nameof(MergePullRequestActivity));
    ((MergePullRequestActivityInput)mergeCall.Input!).BranchName.Should().Be("agent/branch");
    result.Outcome.Should().Be("Done");
}

[Fact]
public async Task Workflow_keeps_polling_when_approved_but_checks_pending()
{
    var ctx = new FakeWorkflowContext();
    SetupHappyPathUntilReviewLoop(ctx);
    ctx.SetReviewPollResults(
        // First poll: approved but checks not green yet → must NOT merge, must loop.
        new WaitForReviewResult(PullRequestReviewState.Approved, false, false, null, DateTimeOffset.UtcNow, Mergeable: null),
        // Second poll (after timer): now green + mergeable → merge + Done.
        new WaitForReviewResult(PullRequestReviewState.Approved, false, true, null, DateTimeOffset.UtcNow, Mergeable: true));
    ctx.AutoCompleteTimers = true;

    var workflow = new DeveloperTaskWorkflow();
    var result = await workflow.RunAsync(ctx, Input());

    ctx.ActivityCalls.Count(c => c.Name == nameof(WaitForReviewActivity)).Should().BeGreaterThanOrEqualTo(2);
    ctx.ActivityCalls.Should().Contain(c => c.Name == nameof(MergePullRequestActivity));
    result.Outcome.Should().Be("Done");
}

[Fact]
public async Task Workflow_leaves_item_in_review_and_reports_MergeFailed_when_not_mergeable()
{
    var ctx = new FakeWorkflowContext();
    SetupHappyPathUntilReviewLoop(ctx);
    ctx.SetActivityResult(nameof(MergePullRequestActivity),
        new MergePullRequestResult(MergeOutcome.NotMergeable));
    ctx.SetReviewPollResults(
        new WaitForReviewResult(PullRequestReviewState.Approved, false, true, null, DateTimeOffset.UtcNow, Mergeable: true));

    var workflow = new DeveloperTaskWorkflow();
    var result = await workflow.RunAsync(ctx, Input());

    result.Outcome.Should().Be("MergeFailed");
    var doneCall = ctx.ActivityCalls.Last(c => c.Name == nameof(DoneActivity));
    var doneInput = (DoneActivityInput)doneCall.Input!;
    doneInput.Success.Should().BeFalse();
    doneInput.PullRequestNumber.Should().Be(7); // non-null PR ⇒ DoneActivity leaves the item In-review
}
```

Update `Workflow_invokes_ModifyCodeActivity_when_ChangesRequested_event_arrives` and `Workflow_calls_WaitForReviewActivity_again_when_timer_elapses`: their second poll result currently is `Approved, Merged:true, ChecksGreen:true` — change it to `Approved, false, true, null, ..., Mergeable: true` so the new gate merges and completes. They will now also call `MergePullRequestActivity`; that is fine (the canned success result is set in `SetupHappyPathUntilReviewLoop`). Also update the `Workflow_passes_plan_pr_number_to_CreatePullRequestActivity` test's `ctx.CompleteExternalEvent("Merged", …)` to `"ReadyToMerge"`.

- [ ] **Step 10: Run them red**

Run: `dotnet test src/Tests/DeveloperAgent.Tests/DeveloperAgent.Tests.csproj --filter "FullyQualifiedName~DeveloperTaskWorkflowReviewLoopTests"`
Expected: FAIL/compile error — workflow still references `MergedEventName`, has no merge wiring, and no `"MergeFailed"` outcome.

- [ ] **Step 11: Rewrite the review-loop branching in `DeveloperTaskWorkflow.cs`**

Replace the **direct-poll decision** block (currently lines ~190–214: the `if (lastReviewResult.Merged && …Approved)` success block followed by the `if (…ChangesRequested)` block) with the following. The `ChangesRequested` block body is unchanged — it is reproduced verbatim so the replacement is a clean copy/paste:

```csharp
// Direct decision when the poll itself observed a terminal state.
if (lastReviewResult.ReviewState == PullRequestReviewState.Approved
    && lastReviewResult.ChecksGreen
    && lastReviewResult.Mergeable == true)
{
    return await CompleteWithMergeAsync(context, input, branchResult,
        acquireResult.BranchName, prNumber, toolCallsUsed, session.Summary, retryOptions);
}

// Approved but GitHub says the PR conflicts → no point waiting; fail to a human.
if (lastReviewResult.ReviewState == PullRequestReviewState.Approved
    && lastReviewResult.Mergeable == false)
{
    return await CompleteWithMergeFailureAsync(context, input, branchResult,
        acquireResult.BranchName, prNumber, toolCallsUsed, session.Summary, retryOptions);
}

if (lastReviewResult.ReviewState == PullRequestReviewState.ChangesRequested)
{
    var modifyResult = await CallModifyCodeAsync(
        context, input, branchResult, acquireResult.BranchName, prNumber,
        lastReviewResult.FeedbackMarkdown ?? string.Empty,
        lastReviewResult.PolledAtUtc, retryOptions);
    await SaveSessionAsync(context, input.ProjectItemId, nameof(ModifyCodeActivity), session.Summary, retryOptions);

    toolCallsUsed += modifyResult.ToolCallsUsed;

    if (modifyResult.Outcome != AgentRunOutcome.Completed)
    {
        return await CompleteWithFailureAsync(context, input, branchResult,
            acquireResult.BranchName, prNumber, toolCallsUsed, session.Summary, retryOptions);
    }

    continue;
}
```

In the **event race** section, rename the merged arm:

```csharp
var readyToMergeEventTask = context.WaitForExternalEventAsync<ReviewEventPayload>(
    WaitForReviewActivity.ReadyToMergeEventName, cts.Token);
// ...
var winner = await Task.WhenAny(changesEventTask, readyToMergeEventTask, timerTask);
cts.Cancel();

if (winner == readyToMergeEventTask)
{
    return await CompleteWithMergeAsync(context, input, branchResult,
        acquireResult.BranchName, prNumber, toolCallsUsed, session.Summary, retryOptions);
}
```

Add the two new helpers (place them next to `CompleteWithSuccessAsync`):

```csharp
/// <summary>
/// Squash-merges the approved PR (and deletes its branch) via <see cref="MergePullRequestActivity"/>,
/// then completes. On a hard merge failure it routes to <see cref="CompleteWithMergeFailureAsync"/>.
/// </summary>
private static async Task<TaskResult> CompleteWithMergeAsync(
    WorkflowContext context, TaskInput input, CreateBranchResult branchResult,
    string branchName, int prNumber, long toolCallsUsed, string? summary, WorkflowTaskOptions retryOptions)
{
    var mergeResult = await context.CallActivityAsync<MergePullRequestResult>(
        nameof(MergePullRequestActivity),
        new MergePullRequestActivityInput(input.ProjectItemId, input.ContentNodeId, prNumber, branchName),
        retryOptions);
    await SaveSessionAsync(context, input.ProjectItemId, nameof(MergePullRequestActivity), summary, retryOptions);

    if (!mergeResult.Succeeded)
    {
        return await CompleteWithMergeFailureAsync(context, input, branchResult,
            branchName, prNumber, toolCallsUsed, summary, retryOptions);
    }

    return await CompleteWithSuccessAsync(context, input, branchResult,
        branchName, prNumber, toolCallsUsed, summary, retryOptions);
}

/// <summary>
/// Hard merge failure: the PR could not be squash-merged (conflict / branch protection). The merge
/// activity has already commented on the PR. Leave the item in In-review for a human (DoneActivity with
/// Success=false and a non-null PR number performs no board transition), release the workspace, and stop.
/// </summary>
private static async Task<TaskResult> CompleteWithMergeFailureAsync(
    WorkflowContext context, TaskInput input, CreateBranchResult branchResult,
    string branchName, int prNumber, long toolCallsUsed, string? summary, WorkflowTaskOptions retryOptions)
{
    var doneInput = new DoneActivityInput(
        ProjectItemId: input.ProjectItemId,
        ContentNodeId: input.ContentNodeId,
        WorkspacePath: branchResult.WorkspacePath,
        BranchName: branchName,
        DefaultBranch: branchResult.DefaultBranch,
        PullRequestNumber: prNumber,
        Success: false,
        ToolCallsUsed: toolCallsUsed);

    await context.CallActivityAsync<object?>(nameof(DoneActivity), doneInput, retryOptions);
    await SaveSessionAsync(context, input.ProjectItemId, nameof(DoneActivity), summary, retryOptions);
    await DeleteSessionAsync(context, input.ProjectItemId, retryOptions);
    return new TaskResult("MergeFailed");
}
```

- [ ] **Step 12: Update the `TaskResult` outcome doc**

In `WorkflowModels.cs`, extend the `TaskResult` summary to mention the new outcome:

```csharp
/// <summary>Final result produced by <see cref="DeveloperTaskWorkflow"/>.</summary>
/// <param name="Outcome">One of "Done", "Failed", "MergeFailed", "Rejected", or "Cancelled".
/// "MergeFailed" means the PR was approved but the squash-merge was refused (conflict / branch
/// protection); the item is left In-review for a human. "Rejected" means the relevance-triage gate
/// parked the item in Backlog before any work began.</param>
public sealed record TaskResult(string Outcome);
```

- [ ] **Step 13: Run the workflow tests green**

Run: `dotnet test src/Tests/DeveloperAgent.Tests/DeveloperAgent.Tests.csproj --filter "FullyQualifiedName~DeveloperTaskWorkflowReviewLoopTests|FullyQualifiedName~WaitForReviewActivityTests"`
Expected: PASS.

- [ ] **Step 14: Commit**

```bash
git add src/DeveloperAgent/Workflow src/Tests/DeveloperAgent.Tests/Workflow
git commit -m "Self-merge approved PRs: act on ReadyToMerge, squash-merge in the review loop"
```

---

## Task 6: Full-suite sweep, stragglers, and docs

**Files:**
- Possibly modify: any remaining test that constructs `WaitForReviewResult`/`PullRequestStatus` or queues the old `"Merged"` event.
- Modify: `CLAUDE.md` is not required; update the spec status note if desired.

- [ ] **Step 1: Grep for stragglers**

Search the solution for any remaining references to the old event name and confirm none remain in production:

Run (PowerShell): `Select-String -Path src -Pattern 'MergedEventName','"Merged"' -SimpleMatch -Recurse | Select-Object Path,LineNumber,Line`
Expected: the only `"Merged"` hits are the `bool Merged` doc-comment in `GitHubModels.cs` and any test deliberately asserting the `PullRequestStatus.Merged` field — **no** `MergedEventName` and **no** `CompleteExternalEvent("Merged", …)` remain.

- [ ] **Step 2: Run the full fast suite**

Run: `dotnet test src/ClaudeAgentsSolo.slnx --filter "Category!=Integration"`
Expected: PASS. Fix any test that fails to compile/await because of the required `Mergeable` fields by appending the appropriate value (`true` for approved-merge fixtures, `null`/`false` otherwise).

- [ ] **Step 3: Confirm the whole solution builds (Release)**

Run: `dotnet build src/ClaudeAgentsSolo.slnx -c Release`
Expected: BUILD SUCCEEDED with no warnings introduced by the change.

- [ ] **Step 4: Commit any straggler fixes**

```bash
git add -A
git commit -m "Fix remaining call sites for self-merge trigger flip"
```

---

## Notes for the implementer

- **Idempotency is the point.** The merge already-merged path is unit-tested at the client level (Task 2); the branch-delete 404 tolerance lives in the transport mirroring `RepositoryExistsAsync`'s existing `NotFoundException` swallow.
- **"Continue with next item" needs no code** — `AgentLifecycleService` grabs the next Ready item when the workflow returns (Done / MergeFailed / Failed alike).
- **The reviewer is unchanged.** `ReviewerAgent` still only posts Approve/RequestChanges; this workflow is the only thing that merges.
- **Mergeability is GitHub-async.** `null` means "still computing" → keep polling; `false` means a real conflict → fail to a human; `true` → merge. Never block forever on `null` (the cadence timer keeps re-polling).
