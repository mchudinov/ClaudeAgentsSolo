# High Level Design

## Recommended runtime flow

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
9. Agent performs coding work through controlled tools.
10. Build/test/PR/review steps are checkpointed.
11. After approval and merge, workflow moves item to Done.
12. Agent writes compacted task memory to Dapr Redis.