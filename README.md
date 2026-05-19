# Developer agent

## Low Level Design

### Core responsibility split

Microsoft Agent Framework = agent abstraction, Anthropic connector, MCP tools, memory hooks
Dapr Workflow             = durable long-running programming-task lifecycle
Dapr Actors               = small stateful coordination actions
Dapr State + Redis         = runtime state, sessions, task state, actor state, workflow state

| Layer                           | Responsibility                                                |
| ------------------------------- | ------------------------------------------------------------- |
| Microsoft Agent Framework       | Creates the Claude-powered developer agent                    |
| Anthropic provider              | Connects the agent to Claude / `claude-opus-4-7`              |
| MCP C# SDK                      | Connects to GitHub MCP and Context7 MCP                       |
| Agent Framework MCP integration | Converts MCP tools into `AITool` objects                      |
| Dapr Workflow                   | Owns the long-running task lifecycle                          |
| Dapr Actor                      | Owns one small stateful unit, usually one GitHub Project item |
| Dapr State API                  | Stores agent/session/task state                               |
| Redis                           | Physical backing store for Dapr state, actors, and workflow   |
| GitHub GraphQL/REST service     | Deterministic project/PR operations                           |
| Build/test runner               | Executes `dotnet restore`, `dotnet build`, `dotnet test`      |
| Policy engine                   | Controls what the agent is allowed to do                      |

### Frameworks details

#### Microsoft Agent Framework

- agent session
- tool calling
- MCP integration
- context providers
- chat history providers
- workflow/checkpoint abstraction

#### Dapr

- persistent state backend
- actor state
- workflow runtime
- Redis/SQL/Cosmos abstraction
- reliability around distributed execution