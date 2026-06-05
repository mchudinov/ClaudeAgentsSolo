// McpToolSource, StdioMcpClientConnector, and IMcpClientConnector are internal — their
// public seam is IMcpToolSource. The library's own test project exercises the internals
// directly; DynamicProxyGenAssembly2 lets a mocking framework proxy internal interfaces.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Agent.Mcp.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DynamicProxyGenAssembly2")]
