# Developer agent

## Requirements

### Development Platform

An AI agent based on Anthropic LLM model. Agent is .NET 10 C# developer.
The agent will be deployed as a Docker container.
Agents must be programmed using C# language and Anthropic .NET SDK.

### AI Agent details

The model must be a configuration option in appsettings. Use claude-opus-4-7 as default.
Make /effort claude code parameter for the agent "xhigh". Make effort a configuration option in the appsettings.
Agent must have GitHub MCP server available. GitHub MCP server is configured through the appsettings.
Agent must have Context7 MCP server available. Context7 MCP server is configured through the appsettings.
The role description for the agent is a separated file in markdown format in /personas/developer.md file.

### Agent in actions

Developer agent picks up items from a GitHUb project. Agent picks only itrems in "Ready" state.
The GitHun project is a configurtation parameter in appsettings file.
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
7. Then Developer agent does /compact command for it's context.
8. Then Developer agent continues with next item.
