// The agent-neutral GitHub layer was extracted to the Agent.GitHub library (Step-37).
// Surfacing it as a global using keeps the per-file using churn out of the ~30 consumer files
// that reference its types (PullRequest, ReviewVerdict, GitHubOptions, IGitHubProjectsClient, …).
global using Agent.GitHub;
// The agent-neutral MCP stdio tool source was extracted to the Agent.Mcp library (Step-45).
// A global using surfaces its types (IMcpToolSource, McpOptions, AddMcpServices, …) unqualified
// and sidesteps the Agent.Mcp vs DeveloperAgent.Agent.Mcp namespace collision the same way the
// other extracted libraries do.
global using Agent.Mcp;
// The agent-neutral memory layer was extracted to the Agent.Memory library (Step-40).
// A global using keeps the per-file churn out of the consumers (the workflow body + the
// session/memory activities) and surfaces the library's types (IAgentMemoryStore,
// IAgentSessionStore, AgentSession, …) unqualified — also sidestepping the Agent.Memory vs
// DeveloperAgent.Agent namespace collision the same way Agent.GitHub does.
global using Agent.Memory;
