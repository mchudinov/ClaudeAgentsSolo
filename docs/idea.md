# Developer agent

## Requirements

### Development Platform

An AI agent based on Anthropic LLM model. Agent is .NET 10 C# developer.
The agent will be deployed as a Docker container.
Agents must be programmed using C# language and Anthropic .NET SDK.
Use GitHub GraphQL API for deterministic project item updates "Ready -> In Progress -> In Review -> Done", create a branch, make a pull request.
Use GitHub MCP for repository and project items exploration.

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

1. Developer agent picks next item from the GitHub project and move item  to the "In progress" state.
2. Developer agent creates a branch and does programming.
3. Once programming is done Developer agent submits a pull request.
4. Then moves the programmed item to the "In Review" state.
5. Developer agent checks for the pull request is approved every 1 minute. It is a confoguration parameter in the appsettings file.
6. Once approved Developer agent moves the item to the "Done" state.
   Move to Done only when:
   - PR is approved by a reviewer
   - CI checks are green
   - branch protection requirements are satisfied
   - PR is merged

7. Then Developer agent does compaction step:
   - summarize completed task
   - summarize changed files
   - summarize decisions
   - summarize test results
   - summarize unresolved risks
   - save compacted memory to state store
8. Then Developer agent continues with next item.
