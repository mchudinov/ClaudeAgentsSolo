# Developer agent

An AI agent ".NET 10 C# developer" based on Anthropic LLM model.

## Requirements

### Development Platform

The agent will be deployed as a Docker container.
Agents must be programmed using C# language and Anthropic .NET SDK.
Use Octokit.GraphQL.NET library for deterministic project item updates "Ready -> In Progress -> In Review -> Done", create a branch, make a pull request, add a comment to an item.
Use GitHub MCP for repository and project items exploration.

Use Dapr Workflow:
   ProgrammingTaskWorkflow
        |
        +-- Activity: acquire task
        +-- Activity: create branch
        +-- Activity: run LLM planning
        +-- Activity: modify code
        +-- Activity: run build/tests
        +-- Activity: create PR
        +-- Wait for external event: PR approved / changes requested
        +-- Activity: move item to Done
        +-- Activity: compact memory

Use Dapr Actor
   ProgrammingTaskActor
        - owns current state
        - protects single-task concurrency with githubProjectItemId
        - exposes status
        - handles reminders/fallback polling

Use Dapr state store Redis for fast runtime state, locks, reminders, agents memory, Dapr Agents and Workflow state.
Use Dapr actor reminders for periodic tasks like:

- Check for new items in "Ready" state
- Cheks for PR approval

Possible Data model ProgrammingTaskActor(githubProjectItemId):

- task state
- branch name
- PR number
- build/test status
- approval status
- failure reason

Dapr supports resiliency policies for timeouts, retries/back-offs, and circuit breakers, and these can be applied to Dapr API calls when calling components. That should be part of the design, around GitHub, Anthropic API calls, MCP calls, and state store operations.

### AI Agent details

Configure Anthropic adaptive thinking / effort level in the appsettings.
Default effort: xhigh.
Default model: claude-opus-4-7.
Agent must have GitHub MCP server available. GitHub MCP server is configured through the appsettings.
Agent must have Context7 MCP server available. Context7 MCP server is configured through the appsettings.
The role description for the agent is a separated file in markdown format in /personas/developer.md file.

### Persistent state

The agent must survive container restart.
Agent stores:

- Current GitHub item ID
- Current branch
- Current PR number
- Current step
- Last completed action
- Compacted memory
- Build/test results
- Error history

### Command sandbox

Add allow/deny rules

Allowed:

- dotnet restore
- dotnet build
- dotnet test
- git status
- git diff
- git checkout
- git commit
- git push

Blocked:

- deleting repo root
- reading secrets
- accessing ~/.ssh
- accessing .env files
- arbitrary curl/wget
- changing CI secrets
- changing branch protection
- force push

### Task scope limits

Add limits on in appsettings:

- Max files changed per task
- Max lines changed per task
- Max execution time
- Max model turns
- Max tool calls
- Max retry count
- Max PR size

### Agent in actions

Developer agent picks up items from a GitHUb project. Agent picks only itrems in "Ready" state.
The GitHub project is a configurtation parameter in appsettings file.
Developer agent identifies what item picks up next.
Developer agent checks for new items in "Ready" state every 1 minute. It is a confoguration parameter in the appsettings file.

### After start

Once started Developer agent checks out Git repo from GitHub that is configured in appserttings.
Developer agent checks if there are any items in "In Review" and wait for the approval.
Developer agent checks if there are any items in "In Progress" state and checks the programming progress, continue if it is not done.

## Usage scenario

1. Developer agent polls GitHub Project for Ready items.
2. Developer agent picks next item from the GitHub project and move item  to the "In progress" state.
3. Developer agent creates a task branch and
4. Developer agent analyses the issue, repository, relevant docs, and coding conventions.
5. Developer agent creates an implementation plan and adds it to the item as comment.
6. Developer agent modifies code in a sandboxed workspace.
7. Developer agent runs build, tests, and static checks.
8. Developer agent commits changes.
9. Developer agent pushes branch.
10. Developer agent creates a pull request.
11. Developer agent moves the item to the "In Review" state.
12. Developer agent waits for the pull request is approved. Agent cheks it every 1 minute. It is a confoguration parameter in the appsettings file.
13. If changes are requested, agent continues on the same branch.
14. Once approved Developer agent moves the item to the "Done" state.
      Move to Done only when:
      - PR is approved by a reviewer
      - CI checks are green
      - branch protection requirements are satisfied
      - PR is merged
15. Then Developer agent does compaction step:
       - summarize completed task
       - summarize changed files
       - summarize decisions
       - summarize test results
       - summarize unresolved risks
       - save compacted memory to state store
16. Then Developer agent continues with next Ready item.
