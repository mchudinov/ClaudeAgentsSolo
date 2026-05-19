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
    Task MarkWaitingForReviewAsync();
    Task MarkApprovedAsync();
    Task MarkChangesRequestedAsync();
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

1. Worker starts.
2. Worker loads config from appsettings
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
9. Agent performs coding work through controlled tools.
10. Build/test/PR/review steps are checkpointed.
11. After approval and merge, workflow moves item to Done.
12. Agent writes compacted task memory to Dapr Redis.
