# Low Level Software Design

## Development Platform

### Software frameworks

The agent will be deployed as a Docker container.
Agents must be programmed using C# language and Microsoft Agent Framework library.

Use **Octokit.GraphQL.NET** library for deterministic tasks:

- moving GitHub Project item states
- creating branches
- pushing commits
- creating PRs
- checking approval/CI
- marking task Done
- adding comments to an item

Use **GitHub MCP** for repository and project items exploration:

- reading repository context
- reading issues/PRs

### Dapr Workflow as the long-running task controller

Dapr Workflows use actors internally and store workflow state in the configured actor state store.
One GitHub Project item should become one workflow instance:

```text
Workflow instance ID:
  github-project-item-{itemId}

Workflow:
  DeveloperTaskWorkflow
```

Use **Dapr Workflow**
   *ProgrammingTaskWorkflow* responcibilities

- Activity: acquire task
- Activity: create branch
- Activity: run LLM planning
- Activity: modify code
- Activity: run build/tests
- Activity: create PR
- Wait for external event: PR approved / changes requested
- Activity: move item to Done
- Activity: compact memory

### Dapr Actors for small stateful actions

Use **Dapr Actor**
   *ProgrammingTaskActor* responcibilities:

- Claim task
- Store current phase
- Store current branch
- Store PR number
- Store current agent session key
- Store retry count
- Store approval status
- Store last known GitHub Project state
- Prevent two containers from working on the same item
- Handle small reminders / fallback checks

Example actor methods:

```C#
public interface IProgrammingTaskActor : IActor
{
    Task<bool> TryClaimAsync(string agentId);
    Task SetPhaseAsync(TaskPhase phase);
    Task SaveBranchAsync(string branchName);
    Task SavePullRequestAsync(int pullRequestNumber);
    Task MarkWaitingForReviewAsync();           # Mark this programming task as waiting for review in the agent’s internal state.
    Task MarkApprovedAsync();                   # Mark this programming task as approved in the agent’s internal state.
    Task MarkChangesRequestedAsync();           # Record that a review is requested changes on the pull request, so the agent must continue working on the same task and branch.
    Task<ProgrammingTaskState> GetStateAsync();
}
```

Use **Dapr Actors** reminders for periodic tasks like:

- Check for new items in "Ready" state
- Cheks for PR approval

### Dapr Redis as the state backend

Use **Dapr state store Redis** for fast runtime state, reminders, agents memory, Dapr Actors and Workflow state.

```YAML
apiVersion: dapr.io/v1alpha1
kind: Component
metadata:
  name: agent-state
spec:
  type: state.redis
  version: v1
  metadata:
    - name: redisHost
      value: "localhost:6379"
    - name: actorStateStore
      value: "true"
auth:
  secretStore: kubernetes
```

#### State model in Redis

Use clear key namespaces.

```text
agent-session:{agentId}:{projectItemId}
  Serialized AgentSession

chat-history:{agentId}:{projectItemId}
  Messages or compacted message chunks

task-state:{projectItemId}
  Current task state

task-memory:{projectItemId}
  Compacted final memory

repo-state:{repoId}
  Repo conventions, project settings, default branch

agent-global:{agentId}
  Current status, last heartbeat, current task

actor state:
  ProgrammingTaskActor/{projectItemId}
  Stored by Dapr actor runtime

workflow state:
  DeveloperTaskWorkflow/{projectItemId}
  Stored by Dapr Workflow runtime
```

### Agent Framework memory design

Use three memory layers.

#### Layer 1: Agent session

```text
AgentSession
  -> serialized into Dapr state
  -> key: agent-session:{agentId}:{projectItemId}
```

#### Layer 2: Chat history provider

```C#
DaprChatHistoryProvider : ChatHistoryProvider
```

It stores and loads chat history from Dapr Redis.

Microsoft’s storage docs explicitly say that for database, Redis, or blob-backed history, you should implement a custom ChatHistoryProvider; the provider should store messages under a session-scoped key and keep returned history within model context limits.

#### Layer 3: AI context provider

```C#
DaprAgentMemoryContextProvider : AIContextProvider
```

It injects relevant memories before the model call and saves useful memories after the model call.

Agent Framework’s context provider docs say custom AIContextProvider implementations are used when you need to inject dynamic instructions/messages/tools or extract state after runs.

Example memories:

- Repository uses xUnit.
- Do not edit generated migration files manually.
- PRs require two approvals.
- Main branch is protected.
- Use nullable reference types.
- This service uses vertical slice architecture.
- Use FluentValidation for request validation.

### Resiliency policies

Dapr supports resiliency policies for timeouts, retries/back-offs, and circuit breakers, and these can be applied to Dapr API calls when calling components. That should be part of the design, around GitHub, Anthropic API calls, MCP calls, and state store operations.

### Example appsettings.json

```json
{
  "Agent": {
    "Name": "DeveloperAgent",
    "Model": "claude-opus-4-7",
    "Effort": "xhigh",
    "PersonaPath": "/personas/developer.md",
    "PollIntervalSeconds": 60,
    "ReviewPollIntervalSeconds": 60
  },
  "Anthropic": {
    "ApiKeySecretName": "anthropic-api-key"
  },
  "GitHub": {
    "Owner": "my-org",

    "Repository": {
      "Name": "my-repo",
      "Url": "https://github.com/my-org/my-repo",
      "DefaultBranch": "main"
    },

    "Project": {
      "Name": "Developer Agent Backlog",
      "Number": 12,
      "OwnerType": "Organization"
    },

    "States": {
      "Ready": "Ready",
      "InProgress": "In Progress",
      "InReview": "In Review",
      "Done": "Done"
    },

    "TokenSecretName": "github-token"
  },
  "McpServers": {
    "GitHub": {
      "Enabled": true,
      "Transport": "stdio",
      "Command": "npx",
      "Arguments": [
        "-y",
        "@modelcontextprotocol/server-github"
      ]
    },
    "Context7": {
      "Enabled": true,
      "Transport": "stdio",
      "Command": "npx",
      "Arguments": [
        "-y",
        "@upstash/context7-mcp"
      ]
    }
  },
  "Dapr": {
    "StateStoreName": "agent-state",
    "PubSubName": "agent-pubsub"
  },
  "Workspace": {
    "RootPath": "/workspace",
    "MaxChangedFiles": 25,
    "MaxChangedLines": 1200,
    "AllowedCommands": [
      "dotnet restore",
      "dotnet build",
      "dotnet test",
      "git status",
      "git diff",
      "git checkout",
      "git add",
      "git commit",
      "git push"
    ]
  }
}
```

## Recommended runtime flow

The system runs as **two cooperating services** — the **DeveloperAgent** and the
**ReviewerAgent** — both launched by the Aspire AppHost. They do not call each other; they
collaborate **only through GitHub state** (the project-board column plus the pull-request's
review verdict, head SHA, and merged flag). Each polls GitHub on its own cadence, so the
collaboration is polling-based and eventually consistent.

### DeveloperAgent worker

1. Worker starts.
2. Worker loads config from appsettings.
3. Worker starts/connects MCP clients:
   - GitHub MCP
   - Context7 MCP
4. Worker creates Microsoft Agent Framework agent:
   - Anthropic provider
   - claude-opus-4-7
   - developer persona markdown
   - MCP tools
   - Dapr-backed memory providers
5. Worker polls GitHub Project for Ready items.
6. For each Ready item:
   - create/start Dapr Workflow instance
   - workflow id = github-project-item-id
7. Dapr Workflow coordinates the full programming lifecycle.
8. Dapr Actor protects per-item state transitions.
9. Agent performs coding work through controlled tools, opens a PR, and the workflow
   moves the board item to **In Review**.
10. Build/test/PR steps are checkpointed.
11. The workflow enters its **review loop**: `WaitForReviewActivity` polls the PR on a
    one-minute cadence (racing external events against a timer).
12. After approval **and** merge (`Merged && Approved`), the workflow moves the item to Done.
13. Agent writes compacted task memory to Dapr Redis.

### ReviewerAgent worker

The ReviewerAgent is a **stateless** web service — GitHub is the only record of what it has
reviewed, so a restart re-derives its work.

1. Worker starts and loads config from appsettings — the shared `Agent` section (identity, model,
   persona, `PollIntervalSeconds`, mirroring the DeveloperAgent host) plus a `Reviewer` section for
   the review-specific knobs (diff caps, required PR-body sections, idempotency/draft/author filters).
2. Worker creates a Microsoft Agent Framework agent:
   - Anthropic provider
   - reviewer persona markdown
   - a single `submit_review` tool (no repo-mutating tools — review is read-only)
3. `ReviewLifecycleService` polls the repository's **open PRs** every `PollIntervalSeconds`.
4. A PR is **due** when it is not a skipped draft, its author is allowed, and its current
   **head SHA has not yet been reviewed** by the configured reviewer login. Idempotency is
   keyed on `(PR number, head SHA, reviewer login)` — each head SHA is reviewed once.
5. For each due PR, `ReviewerAgent.ReviewAsync` runs two deterministic pre-checks, then a
   model-backed persona scan:
   - **Check 1** — the PR body contains every required section → else `RequestChanges`.
   - **Check 2** — the diff is not oversized (file/line caps) → else `RequestChanges`.
   - **Persona scan** — the agent reads the full diff and calls `submit_review` once.
6. The reviewer posts **one** GitHub review: **Approve** or **RequestChanges**. It fails closed
   (RequestChanges) on any internal error and **never merges** — merging the approved PR is an
   out-of-band human action that the DeveloperAgent's workflow simply observes.
   (A manual `POST /review/{prNumber}` endpoint exists for on-demand reviews.)

### How the two agents collaborate

```text
DeveloperAgent                          GitHub (shared state)               ReviewerAgent
──────────────                          ─────────────────────               ─────────────
opens PR + moves item ──────────────►   PR open @ SHA#1, In Review
                                        PR @ SHA#1 unreviewed       ◄──────  poll picks it up (due)
                                                                             pre-checks + persona scan
WaitForReviewActivity polls    ◄──────  review posted              ◄──────  submit Approve / RequestChanges
  • RequestChanges → ModifyCode
      push fix ─────────────────────►   PR head → SHA#2 ───────────────────► next poll: SHA#2 due → re-review
  • Approved → wait for merge
      (human merges) ◄──────────────    PR merged
  DoneActivity → In Review → Done
  CompactMemory + cleanup
```

- **`RequestChanges` is the iteration signal.** It triggers a fresh DeveloperAgent round
  (`ModifyCodeActivity`) against the same branch; the resulting push produces a new head SHA,
  which makes the PR "due" again and re-arms the reviewer.
- **Approval alone does not finish the task.** The developer's review loop completes only on the
  *merged* state; an approved-but-unmerged PR keeps looping on the cadence timer until merged.
- **No service-to-service messaging.** Neither side subscribes to the other; both reconcile
  against GitHub. (The AppHost wires a Service Bus topic, but it is currently unconsumed
  scaffolding and is not part of this handshake.)
