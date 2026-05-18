# Developer and Code Reviewer agents

## Requirements

Create two AI agents based on Anthropic claude-opus-4-7.
One is .NET 10 C# developer and another one is C# code reviewer.
Both agents will be deployed to Azure AI Foundry.
Both agents must have GitHub MCP server available. GitHub MCP server may be a part of Azure AI Foundry project.
Both agents must be programmed using MS Agentic Framework and C# language.
Make /effort claude code parameter for Developer agent "xhigh" and for Code Reviewer agent "high". Make effort a configuration option for both agents.
Both agent must have memory.
The role description for each agent is a separated file in markdown format.

## Usage scenario

Developer agent is called from Claude Code console. Humam user explicetly asks to use remote Developer agent for C# programming task.
Developer agent checks out Git repo from GitHub that user asked about, does programming and submits a pull request.
Then Developer agent calls the Code Reviewer.
Code Reviewer agent does code review of code changes in the submitted pull request and approves the request.
Then Code Reviewer agent calls the Developer agent about that pull request is approved.
Then Developer agent does /compact command for it's context.
